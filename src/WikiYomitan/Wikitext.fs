/// Turns the raw wikitext of an article into a short plain-text abstract
/// (the lead paragraph) plus a kana reading when one can be extracted.
module WikiYomitan.Wikitext

open System
open System.Text
open System.Text.RegularExpressions

/// A reading that is guaranteed to consist only of kana (+ prolonged sound marks).
[<Struct>]
type Reading =
    private | Reading of string
    member this.Value = let (Reading r) = this in r

module Reading =
    let private kanaOnly = Regex(@"^[ぁ-ゖァ-ヺーゝゞヽヾ]+$", RegexOptions.Compiled)

    /// Accepts a candidate string as a Reading only if, after dropping
    /// spaces and middle dots, it is entirely kana.
    let tryCreate (candidate: string) : Reading option =
        let cleaned = candidate.Replace(" ", "").Replace("　", "").Replace("・", "")
        if cleaned.Length > 0 && kanaOnly.IsMatch cleaned then Some(Reading cleaned) else None

/// The extracted lead of an article. Gloss is never empty.
type Lead =
    private
        { gloss: string
          reading: Reading option }

    member this.Gloss = this.gloss
    member this.Reading = this.reading

let private comments = Regex(@"<!--.*?(-->|$)", RegexOptions.Singleline ||| RegexOptions.Compiled)

let private refs =
    Regex(@"<ref[^<>]*/\s*>|<ref[^<>]*>.*?</ref\s*>", RegexOptions.Singleline ||| RegexOptions.IgnoreCase ||| RegexOptions.Compiled)

/// Extension tags whose *content* is markup or media, not prose.
let private containerTags =
    Regex(
        @"<(imagemap|gallery|timeline|mapframe|graph|score|math|chem|syntaxhighlight|source|pre|code|references)[^>]*>.*?</\1\s*>",
        RegexOptions.Singleline ||| RegexOptions.IgnoreCase ||| RegexOptions.Compiled
    )

let private externalLinks = Regex(@"\[(?:https?|ftp)://[^\s\]]*(?:\s+([^\]]*))?\]", RegexOptions.Compiled)
let private lineBreakTags = Regex(@"<br\s*/?\s*>", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)
let private otherTags = Regex(@"</?[A-Za-z][^<>]*>", RegexOptions.Compiled)
let private magicWords = Regex(@"__\w+__", RegexOptions.Compiled)
let private quotes = Regex(@"'{2,}", RegexOptions.Compiled)

/// 「（、）」-style husks left where parenthesised content was only templates.
/// （） appear in the class so nested husks like 「（（）、）」 go in one match;
/// any real content breaks the match.
let private emptyParens = Regex(@"（[\s、，,・;；:：/／〈〉《》〔〕［］（）]*）", RegexOptions.Compiled)

/// Removes {{templates}} and {|tables|}, including nested ones. Content still
/// inside an unclosed template/table when the input ends (e.g. because the
/// input was truncated) is dropped.
let private stripBracePairs (s: string) =
    let sb = StringBuilder(s.Length)
    let mutable i = 0
    let mutable templateDepth = 0
    let mutable tableDepth = 0

    while i < s.Length do
        let c = s.[i]
        let next = if i + 1 < s.Length then s.[i + 1] else '\000'

        if c = '{' && next = '{' then
            templateDepth <- templateDepth + 1
            i <- i + 2
        elif c = '}' && next = '}' && templateDepth > 0 then
            templateDepth <- templateDepth - 1
            i <- i + 2
        elif c = '{' && next = '|' then
            tableDepth <- tableDepth + 1
            i <- i + 2
        elif c = '|' && next = '}' && tableDepth > 0 then
            tableDepth <- tableDepth - 1
            i <- i + 2
        else
            if templateDepth = 0 && tableDepth = 0 then sb.Append c |> ignore
            i <- i + 1

    sb.ToString()

let private readingTemplateNames = [| "読み仮名"; "読み仮名 ruby不使用"; "ルビ"; "ruby" |]

/// Expands 「{{読み仮名|'''東京都'''|とうきょうと|…}}」 (and {{ruby}} variants) to
/// 「'''東京都'''（とうきょうと）」 so the reading survives template stripping.
/// Arguments are split at top level only; nested templates stay intact for
/// later passes.
let private expandReadingTemplates (s: string) =
    let sb = StringBuilder(s.Length)
    let mutable i = 0

    while i < s.Length do
        let mutable expanded = false

        if i + 1 < s.Length && s.[i] = '{' && s.[i + 1] = '{' then
            let mutable nameEnd = i + 2

            while nameEnd < s.Length
                  && nameEnd - i < 40
                  && not (List.contains s.[nameEnd] [ '|'; '{'; '}' ]) do
                nameEnd <- nameEnd + 1

            let name = s.Substring(i + 2, nameEnd - i - 2).Trim().Replace('_', ' ')

            let isReadingTemplate =
                readingTemplateNames
                |> Array.exists (fun n -> String.Equals(name, n, StringComparison.OrdinalIgnoreCase))

            if isReadingTemplate && nameEnd < s.Length && s.[nameEnd] = '|' then
                let args = ResizeArray<StringBuilder>()
                args.Add(StringBuilder())
                let mutable depth = 1
                let mutable j = nameEnd + 1
                let mutable closed = false

                while not closed && j < s.Length do
                    let c = s.[j]
                    let next = if j + 1 < s.Length then s.[j + 1] else '\000'

                    if c = '{' && next = '{' then
                        depth <- depth + 1
                        args.[args.Count - 1].Append("{{") |> ignore
                        j <- j + 2
                    elif c = '}' && next = '}' then
                        depth <- depth - 1

                        if depth = 0 then
                            closed <- true
                        else
                            args.[args.Count - 1].Append("}}") |> ignore

                        j <- j + 2
                    elif c = '|' && depth = 1 then
                        args.Add(StringBuilder())
                        j <- j + 1
                    else
                        args.[args.Count - 1].Append c |> ignore
                        j <- j + 1

                if closed then
                    sb.Append(args.[0].ToString()) |> ignore

                    if args.Count >= 2 && args.[1].Length > 0 then
                        sb.Append('（').Append(args.[1].ToString()).Append('）') |> ignore

                    i <- j
                    expanded <- true
                else
                    // unterminated (truncated input): drop the rest
                    i <- s.Length
                    expanded <- true

        if not expanded then
            sb.Append s.[i] |> ignore
            i <- i + 1

    sb.ToString()

let private droppedLinkPrefixes =
    [ "file:"; "image:"; "media:"; "ファイル:"; "画像:"; "category:"; "カテゴリ:" ]

/// Resolves [[wiki links]]: file/image/category links are dropped whole
/// (matching nested brackets, since captions may contain links); ordinary
/// links are replaced by their label (or target when there is no label).
let private resolveWikiLinks (s: string) =
    let sb = StringBuilder(s.Length)
    let mutable i = 0

    while i < s.Length do
        if i + 1 < s.Length && s.[i] = '[' && s.[i + 1] = '[' then
            // find the matching ]] allowing nested [[ ]]
            let mutable depth = 1
            let mutable j = i + 2

            while depth > 0 && j + 1 < s.Length do
                if s.[j] = '[' && s.[j + 1] = '[' then
                    depth <- depth + 1
                    j <- j + 2
                elif s.[j] = ']' && s.[j + 1] = ']' then
                    depth <- depth - 1
                    j <- j + 2
                else
                    j <- j + 1

            if depth > 0 then
                // unterminated link (truncated input): drop the rest
                i <- s.Length
            else
                let inner = s.Substring(i + 2, j - i - 4)
                let lowered = inner.TrimStart(' ', '　', ':').ToLowerInvariant()

                if droppedLinkPrefixes |> List.exists (fun (p: string) -> lowered.StartsWith p) then
                    () // dropped entirely
                else
                    let label =
                        match inner.LastIndexOf '|' with
                        | -1 ->
                            match inner.IndexOf '#' with
                            | -1 -> inner
                            | h -> inner.Substring(0, h)
                        | p -> inner.Substring(p + 1)

                    sb.Append label |> ignore

                i <- j
        else
            sb.Append s.[i] |> ignore
            i <- i + 1

    sb.ToString()

let private decodeEntities (s: string) =
    s
        .Replace("&nbsp;", " ")
        .Replace("&amp;", "&")
        .Replace("&lt;", "<")
        .Replace("&gt;", ">")
        .Replace("&quot;", "\"")
        .Replace("&#39;", "'")

/// A line is part of the lead paragraph text only if it is not list/table/heading markup.
let private isProseLine (line: string) =
    line.Length > 0
    && not (List.contains line.[0] [ '*'; '#'; ':'; ';'; '='; '|'; '!' ])

let private maxGlossLength = 500

/// Cuts overlong glosses where a reader would: at the last sentence end (。)
/// outside parentheses, then at a top-level 、, then just before a
/// parenthetical the cut would otherwise strand open, and hard as a last
/// resort. Very early boundaries (< a third in) don't count.
let private truncateAtSentence (s: string) =
    if s.Length <= maxGlossLength then
        s
    else
        let head = s.Substring(0, maxGlossLength)

        let lastTopLevel isBoundary =
            let mutable depth = 0
            let mutable result = -1

            for i in 0 .. head.Length - 1 do
                match head.[i] with
                | '（'
                | '(' -> depth <- depth + 1
                | '）'
                | ')' -> depth <- max 0 (depth - 1)
                | c ->
                    if depth = 0 && isBoundary c then
                        result <- i

            result

        let minKeep = maxGlossLength / 3
        let sentenceEnd = lastTopLevel (fun c -> c = '。')

        if sentenceEnd >= minKeep then
            head.Substring(0, sentenceEnd + 1)
        else
            let clauseEnd = lastTopLevel (fun c -> c = '、' || c = '，')

            if clauseEnd >= minKeep then
                head.Substring(0, clauseEnd) + "…"
            else
                let unclosedParenStart =
                    let mutable stack = []

                    for i in 0 .. head.Length - 1 do
                        match head.[i] with
                        | '（'
                        | '(' -> stack <- i :: stack
                        | '）'
                        | ')' ->
                            match stack with
                            | _ :: rest -> stack <- rest
                            | [] -> ()
                        | _ -> ()

                    match List.rev stack with
                    | first :: _ -> first
                    | [] -> -1

                if unclosedParenStart >= minKeep then
                    head.Substring(0, unclosedParenStart).TrimEnd([| '、'; '，'; ' '; '　' |]) + "…"
                else
                    head + "…"

let private baseSeparators = [| '、'; '，'; ','; '/'; '／'; '；'; ';' |]
let private separatorsWithDot = Array.append baseSeparators [| '・' |]

/// First kana-only candidate among 「（しゅうこう、…）」-style paren content.
/// `splitDots` treats ・ as separating alternate readings (「（とだ…・へだ…）」)
/// rather than joining segments of one reading (「（おおつか・さいかちど…）」).
let private readingFromParens (splitDots: bool) (gloss: string) (openIdx: int) =
    if openIdx < 0 || openIdx >= gloss.Length || gloss.[openIdx] <> '（' then
        None
    else
        match gloss.IndexOf('）', openIdx) with
        | -1 -> None
        | closeIdx ->
            gloss
                .Substring(openIdx + 1, closeIdx - openIdx - 1)
                .Split(if splitDots then separatorsWithDot else baseSeparators)
            |> Array.tryPick Reading.tryCreate

/// Extracts the reading from a lead like 「周溝（しゅうこう、…）は…」:
/// the first kana-only candidate inside the first （…） near the start.
let private extractReading (gloss: string) =
    let openIdx = gloss.IndexOf '（'
    if openIdx > 60 then None else readingFromParens false gloss openIdx

let private isKanji (c: char) =
    (c >= '一' && c <= '鿿') || (c >= '㐀' && c <= '䶿') || c = '々' || c = '〆'

// Letters only: ・ (U+30FB) and ゠ (U+30A0) are punctuation despite living
// in the katakana block, and get their own treatment as name joiners.
let private isKatakana (c: char) =
    (c >= 'ァ' && c <= 'ヺ') || c = 'ー' || c = 'ヽ' || c = 'ヾ' || c = 'ヿ'

/// Finds the reading a lead gives for a *mentioned* term, e.g. 周溝's lead
/// 「…周濠（しゅうごう）とする場合もある」 yields しゅうごう for 周濠. Used for
/// redirect titles, whose reading often differs from their target's.
/// A match embedded at the end of a longer word or compound (中央競馬会 inside
/// 日本中央競馬会（にっぽん…）, 青い州 inside 赤い州・青い州（…）, Home inside
/// PlayStation Home（…）) would steal the longer name's reading, so matches
/// whose preceding character continues a word — kanji, ・/＝, same-script
/// katakana, or latin/space before a latin term — are rejected.
let findTermReading (term: string) (gloss: string) : Reading option =
    // A character that can continue a name across the match boundary.
    let wordy c = isKanji c || isKatakana c || Char.IsAsciiLetterOrDigit c

    let embedded i =
        if i = 0 then
            false
        else
            match gloss.[i - 1] with
            // 日本中央競馬会, バス北遠本線, PlayStation2: the match continues
            // a longer name, whose reading would be stolen
            | prev when wordy prev -> true
            // ・/＝ joins a compound (赤い州・青い州（…）) — but after a
            // 「）」 it merely separates enumerated variants, each carrying
            // its own reading: 周堀（しゅうごう）・周濠（しゅうごう）.
            | '・'
            | '＝'
            | '゠' -> not (i >= 2 && gloss.[i - 2] = '）')
            // 「伊藤 若冲（いとう じゃくちゅう…）」: the space in the
            // full-name convention still joins one name
            | ' '
            | '　' -> i >= 2 && wordy gloss.[i - 2]
            | _ -> false

    // ・ between kana candidates separates alternate readings of a kanji term
    // (戸田中学校（とだ…・へだ…）), but segments one long reading of a term
    // that itself contains ・ or latin words (SAPPORO BEER OTOAJITO
    // （サッポロビール・オトアジート）).
    let splitDots = not (term.Contains '・') && Seq.exists isKanji term

    let rec searchFrom start =
        match gloss.IndexOf(term, start, StringComparison.Ordinal) with
        | -1 -> None
        | i ->
            match (if embedded i then None else readingFromParens splitDots gloss (i + term.Length)) with
            | Some r -> Some r
            | None -> searchFrom (i + term.Length)

    if term.Length = 0 then None else searchFrom 0

/// How much raw wikitext to look at. Leads sit behind (sometimes very large)
/// infoboxes, so this must comfortably exceed common infobox sizes.
let private rawWindow = 60_000

let extractLead (wikitext: string) : Lead option =
    let raw =
        if wikitext.Length > rawWindow then wikitext.Substring(0, rawWindow) else wikitext

    let replace (re: Regex) (replacement: string) (s: string) = re.Replace(s, replacement)

    let text =
        raw
        |> replace comments ""
        |> replace refs ""
        |> replace containerTags ""
        |> expandReadingTemplates
        |> stripBracePairs
        |> resolveWikiLinks
        |> replace externalLinks "$1"
        |> replace lineBreakTags " "
        |> replace otherTags ""
        |> replace magicWords ""
        |> replace quotes ""
        |> replace emptyParens ""
        |> decodeEntities

    let paragraph =
        text.Split '\n'
        |> Seq.map (fun l -> l.Trim())
        |> Seq.skipWhile (isProseLine >> not)
        |> Seq.takeWhile isProseLine
        |> String.concat ""

    let gloss = truncateAtSentence (paragraph.Trim())

    if gloss.Length < 5 then
        None
    else
        Some { gloss = gloss; reading = extractReading gloss }
