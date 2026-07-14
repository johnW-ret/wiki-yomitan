# jawiki-yomitan

Builds a [Yomitan](https://yomitan.wiki/) dictionary from an official
[Wikimedia jawiki dump](https://dumps.wikimedia.org/jawiki/): one entry per
main-namespace article containing the lead paragraph, a kana reading when the
lead declares one (「'''周溝'''（しゅうこう）は、…」 or `{{読み仮名}}`), and a link to
the article. Redirect titles (e.g. 周濠 → 周溝) become entries that point at
their target and quote its abstract.

This exists because the DBPedia abstract dumps that
[MarvNC/wikipedia-yomitan](https://github.com/MarvNC/wikipedia-yomitan) was
built on stopped updating in December 2022. This tool needs only the standard
`pages-articles` dump, which Wikimedia publishes continuously.

## Usage

```sh
curl -O https://dumps.wikimedia.org/jawiki/latest/jawiki-latest-pages-articles-multistream.xml.bz2
bzcat jawiki-latest-pages-articles-multistream.xml.bz2 | \
  dotnet run -c Release --project src/JawikiYomitan -- jawiki.zip --date 2026-07-06
```

Takes roughly 15 minutes and ~2 GB of RAM for the full dump (~2.4 M pages).
`--limit N` stops after N pages, for smoke tests.

Import the resulting zip in Yomitan via Settings → Dictionaries → Import.

## Design

- `Wikitext.fs` — wikitext → plain-text lead + optional `Reading`
  (a private single-case DU that can only hold pure kana).
- `Dump.fs` — streams `Article`/`Redirect` pages (a DU: a redirect can never be
  mistaken for an article) out of the dump XML.
- `Yomitan.fs` — streams entries into `term_bank_N.json` files inside the zip.
- Single pass over the dump: article entries are written as they stream by;
  redirect entries are resolved (up to 3 hops) afterwards from an in-memory
  map of article excerpts.

## Licensing

The code is MIT. The *output* contains Wikipedia article text, which is
[CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/); the generated
`index.json` carries the attribution. Keep the attribution if you share a
generated dictionary.
