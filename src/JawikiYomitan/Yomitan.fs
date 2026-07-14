/// Writes Yomitan dictionary zips (format 3: index.json + term_bank_N.json).
module JawikiYomitan.Yomitan

open System
open System.IO
open System.IO.Compression
open System.Text.Json
open System.Text.Encodings.Web

/// Write Japanese text as UTF-8, not \uXXXX escapes.
let private writerOptions = JsonWriterOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

/// What a dictionary entry says. An article entry carries its own gloss; a
/// redirect entry can only ever point at a target and quote that target's gloss.
type EntryKind =
    | ArticleEntry of gloss: string
    | RedirectEntry of target: string * targetGloss: string

type Entry =
    { /// The text Yomitan matches on (disambiguation suffix already stripped).
      Term: string
      /// Kana reading, or "" when unknown.
      Reading: string
      /// The full article title, used to build the Wikipedia URL.
      ArticleTitle: string
      Kind: EntryKind }

let articleUrl (title: string) =
    "https://ja.wikipedia.org/wiki/" + Uri.EscapeDataString title

let private writeLink (w: Utf8JsonWriter) (url: string) (text: string) =
    w.WriteStartObject()
    w.WriteString("tag", "a")
    w.WriteString("href", url)
    w.WriteString("content", text)
    w.WriteEndObject()

let private writeStructuredContent (w: Utf8JsonWriter) (entry: Entry) =
    w.WriteStartObject()
    w.WriteString("type", "structured-content")
    w.WritePropertyName "content"
    w.WriteStartArray()

    match entry.Kind with
    | ArticleEntry gloss ->
        w.WriteStartObject()
        w.WriteString("tag", "div")
        w.WriteString("content", gloss)
        w.WriteEndObject()
    | RedirectEntry(target, targetGloss) ->
        w.WriteStartObject()
        w.WriteString("tag", "div")
        w.WritePropertyName "content"
        w.WriteStartArray()
        w.WriteStringValue "→ "
        writeLink w (articleUrl target) target
        w.WriteEndArray()
        w.WriteEndObject()

        if targetGloss <> "" then
            w.WriteStartObject()
            w.WriteString("tag", "div")
            w.WriteString("content", targetGloss)
            w.WriteEndObject()

    w.WriteStartObject()
    w.WriteString("tag", "div")
    w.WritePropertyName "content"
    w.WriteStartArray()
    writeLink w (articleUrl entry.ArticleTitle) "Wikipedia"
    w.WriteEndArray()

    w.WritePropertyName "data"
    w.WriteStartObject()
    w.WriteString("wikipedia", "attribution")
    w.WriteEndObject()
    w.WriteEndObject()

    w.WriteEndArray()
    w.WriteEndObject()

let private writeRow (w: Utf8JsonWriter) (sequence: int) (entry: Entry) =
    w.WriteStartArray()
    w.WriteStringValue entry.Term
    w.WriteStringValue entry.Reading
    w.WriteStringValue "" // definition tags
    w.WriteStringValue "" // deinflection rules
    w.WriteNumberValue 0 // score
    w.WriteStartArray()
    writeStructuredContent w entry
    w.WriteEndArray()
    w.WriteNumberValue sequence
    w.WriteStringValue "" // term tags
    w.WriteEndArray()

/// Streams entries into term banks of `bankSize` inside a single zip.
/// Returns the number of entries written.
let write (outputPath: string) (dumpDate: string) (bankSize: int) (entries: Entry seq) : int =
    use zip = new ZipArchive(File.Create outputPath, ZipArchiveMode.Create)

    let mutable bankIndex = 0
    let mutable inBank = 0
    let mutable sequence = 0
    let mutable writer: (Utf8JsonWriter * Stream) option = None

    let closeBank () =
        writer
        |> Option.iter (fun (w, stream) ->
            w.WriteEndArray()
            w.Dispose()
            stream.Dispose())

        writer <- None

    for entry in entries do
        if writer.IsNone then
            bankIndex <- bankIndex + 1
            inBank <- 0
            let e = zip.CreateEntry($"term_bank_{bankIndex}.json", CompressionLevel.Optimal)
            let stream = e.Open()
            let w = new Utf8JsonWriter(stream, writerOptions)
            w.WriteStartArray()
            writer <- Some(w, stream)

        sequence <- sequence + 1
        inBank <- inBank + 1
        writeRow (fst writer.Value) sequence entry

        if inBank >= bankSize then closeBank ()

    closeBank ()

    let indexEntry = zip.CreateEntry("index.json", CompressionLevel.Optimal)
    use iw =
        new Utf8JsonWriter(
            indexEntry.Open(),
            JsonWriterOptions(Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
        )
    iw.WriteStartObject()
    iw.WriteString("title", $"JA Wikipedia [{dumpDate}]")
    iw.WriteString("revision", $"jawiki-{dumpDate};jawiki-yomitan")
    iw.WriteNumber("format", 3)
    iw.WriteBoolean("sequenced", true)
    iw.WriteString("author", "jawiki-yomitan (local build)")
    iw.WriteString("url", "https://dumps.wikimedia.org/jawiki/")

    iw.WriteString(
        "description",
        $"Lead-paragraph abstracts of every ja.wikipedia.org article (dump of {dumpDate}), including redirect titles. Built locally with jawiki-yomitan."
    )

    iw.WriteString(
        "attribution",
        "Text from Wikipedia, the free encyclopedia (ja.wikipedia.org), by Wikipedia contributors. Licensed under CC BY-SA 4.0."
    )

    iw.WriteString("sourceLanguage", "ja")
    iw.WriteString("targetLanguage", "ja")
    iw.WriteEndObject()

    sequence
