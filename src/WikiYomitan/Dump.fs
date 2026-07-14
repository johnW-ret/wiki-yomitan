/// Streams pages out of a MediaWiki pages-articles XML dump.
module WikiYomitan.Dump

open System.Xml

/// A main-namespace page, already classified: redirects can never be
/// mistaken for articles and carry no wikitext.
type Page =
    | Article of title: string * wikitext: string
    | Redirect of source: string * target: string

/// Lazily yields every main-namespace (ns 0) page in the dump XML on `stream`.
let readPages (stream: System.IO.Stream) : Page seq =
    seq {
        let settings = XmlReaderSettings(IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore)
        use reader = XmlReader.Create(stream, settings)

        let mutable title = ""
        let mutable ns = "0"
        let mutable redirectTarget = None
        let mutable text = ""

        let mutable hasNode = reader.Read()

        while hasNode do
            // ReadElementContentAsString leaves the reader on the node *after*
            // the element, which must itself be processed, not skipped by Read.
            let mutable consumed = false

            if reader.NodeType = XmlNodeType.Element then
                match reader.LocalName with
                | "page" ->
                    title <- ""
                    ns <- "0"
                    redirectTarget <- None
                    text <- ""
                | "title" ->
                    title <- reader.ReadElementContentAsString()
                    consumed <- true
                | "ns" ->
                    ns <- reader.ReadElementContentAsString()
                    consumed <- true
                | "redirect" -> redirectTarget <- Some(reader.GetAttribute "title")
                | "text" ->
                    text <- reader.ReadElementContentAsString()
                    consumed <- true
                | _ -> ()
            elif reader.NodeType = XmlNodeType.EndElement && reader.LocalName = "page" && ns = "0" then
                match redirectTarget with
                | Some target when not (isNull target) -> yield Redirect(title, target)
                | _ -> yield Article(title, text)

            hasNode <- if consumed then not reader.EOF else reader.Read()
    }
