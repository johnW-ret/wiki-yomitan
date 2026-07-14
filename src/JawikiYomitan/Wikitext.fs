/// Turns the raw wikitext of an article into a short plain-text abstract
/// (the lead paragraph) plus a kana reading when one can be extracted.
module JawikiYomitan.Wikitext

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
let private magicWords = Regex(@"__[A-Z]+__", RegexOptions.Compiled)
let private quotes = Regex(@"'{2,}", RegexOptions.Compiled)

/// 「（、）」-style husks left where parenthesised content was only templates.
let private emptyParens = Regex(@"（[\s、，,・;；:：/／〈〉《》〔〕［］]*）", RegexOptions.Compiled)

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

let private truncateAtSentence (s: string) =
    if s.Length <= maxGlossLength then
        s
    else
        let head = s.Substring(0, maxGlossLength)

        match head.LastIndexOf '。' with
        | -1 -> head + "…"
        | i -> head.Substring(0, i + 1)

/// Extracts the reading from a lead like 「周溝（しゅうこう、…）は…」:
/// the first kana-only candidate inside the first （…） near the start.
let private extractReading (gloss: string) =
    let openIdx = gloss.IndexOf '（'

    if openIdx < 0 || openIdx > 60 then
        None
    else
        match gloss.IndexOf('）', openIdx) with
        | -1 -> None
        | closeIdx ->
            gloss.Substring(openIdx + 1, closeIdx - openIdx - 1).Split([| '、'; '，'; ','; '/'; '／'; '；'; ';' |])
            |> Array.tryPick Reading.tryCreate

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
