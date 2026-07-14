module JawikiYomitan.Program

open System
open System.Collections.Generic
open System.Text.RegularExpressions
open JawikiYomitan

/// Kept per article so redirect entries can quote their target.
[<Struct>]
type private ArticleRecord = { Excerpt: string; Reading: string }

let private disambigSuffix = Regex(@"\s*\([^()]*\)$", RegexOptions.Compiled)

/// "円城寺 (飛騨市神岡町船津)" is looked up as "円城寺"; the full title stays in the link.
let private lookupTerm (title: string) =
    let stripped = disambigSuffix.Replace(title, "")
    if stripped.Length > 0 then stripped else title

let private excerptLength = 200

let private excerpt (gloss: string) =
    if gloss.Length <= excerptLength then gloss else gloss.Substring(0, excerptLength) + "…"

type private Options =
    { OutputPath: string
      DumpDate: string
      Limit: int option }

let private parseArgs (argv: string array) =
    let rec go opts =
        function
        | "--limit" :: n :: rest -> go { opts with Limit = Some(int n) } rest
        | "--date" :: d :: rest -> go { opts with DumpDate = d } rest
        | out :: rest -> go { opts with OutputPath = out } rest
        | [] -> opts

    go
        { OutputPath = "jawiki-yomitan.zip"
          DumpDate = DateOnly.FromDateTime(DateTime.Today).ToString "yyyy-MM-dd"
          Limit = None }
        (List.ofArray argv)

[<EntryPoint>]
let main argv =
    let options = parseArgs argv

    let articles = Dictionary<string, ArticleRecord>()
    let redirects = List<struct (string * string)>()
    let mutable pagesSeen = 0

    let pages =
        Dump.readPages (Console.OpenStandardInput())
        |> match options.Limit with
           | Some n -> Seq.truncate n
           | None -> id

    let articleEntries =
        seq {
            for page in pages do
                pagesSeen <- pagesSeen + 1

                if pagesSeen % 100_000 = 0 then
                    eprintfn $"...{pagesSeen} pages ({articles.Count} articles, {redirects.Count} redirects)"

                match page with
                | Dump.Redirect(source, target) -> redirects.Add(struct (source, target))
                | Dump.Article(title, wikitext) ->
                    match Wikitext.extractLead wikitext with
                    | None -> ()
                    | Some lead ->
                        let reading =
                            lead.Reading |> Option.map _.Value |> Option.defaultValue ""

                        articles[title] <- { Excerpt = excerpt lead.Gloss; Reading = reading }

                        yield
                            { Yomitan.Term = lookupTerm title
                              Yomitan.Reading = reading
                              Yomitan.ArticleTitle = title
                              Yomitan.Kind = Yomitan.ArticleEntry lead.Gloss }
        }

    // Redirect entries can only be built after every page has been seen, so
    // they are appended lazily behind the article stream.
    let redirectEntries =
        seq {
            let redirectMap = Dictionary<string, string>()

            for struct (source, target) in redirects do
                redirectMap[source] <- target

            // A redirect's target may itself be a redirect; follow a few hops.
            let resolve target =
                let mutable current = target
                let mutable hops = 0

                while hops < 3 && not (articles.ContainsKey current) && redirectMap.ContainsKey current do
                    current <- redirectMap[current]
                    hops <- hops + 1

                if articles.ContainsKey current then Some current else None

            for struct (source, target) in redirects do
                match resolve target with
                | None -> ()
                | Some resolved ->
                    let record = articles[resolved]

                    yield
                        { Yomitan.Term = lookupTerm source
                          Yomitan.Reading = ""
                          Yomitan.ArticleTitle = source
                          Yomitan.Kind = Yomitan.RedirectEntry(resolved, record.Excerpt) }
        }

    let total =
        Seq.append articleEntries redirectEntries
        |> Yomitan.write options.OutputPath options.DumpDate 25_000

    eprintfn
        $"done: {pagesSeen} pages -> {articles.Count} article entries + {total - articles.Count} redirect entries -> {options.OutputPath}"

    0
