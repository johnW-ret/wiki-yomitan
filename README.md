# wiki-yomitan

Builds a [Yomitan](https://yomitan.wiki/) dictionary from an official
[Wikimedia dump](https://dumps.wikimedia.org/): one entry per main-namespace
article containing the lead paragraph, a kana reading when the lead declares
one (「'''周溝'''（しゅうこう）は、…」 or `{{読み仮名}}`), and a link to the
article. Redirect titles (e.g. 周濠 → 周溝) become entries that point at their
target and quote its abstract — with the redirect's own reading when the
target's lead states it (周濠【しゅうごう】).

**Download the latest Japanese Wikipedia dictionary from the
[releases page](https://github.com/johnW-ret/wiki-yomitan/releases)** and
import it in Yomitan via Settings → Dictionaries → Import.

This exists because the DBPedia abstract dumps that
[MarvNC/wikipedia-yomitan](https://github.com/MarvNC/wikipedia-yomitan) was
built on stopped updating in December 2022. This tool needs only the standard
`pages-articles` dump, which Wikimedia publishes continuously.

Currently only Japanese Wikipedia is built and released: the lead-parsing
heuristics (readings, fullwidth parens, 読み仮名 templates) are ja-specific,
though the dump/zip plumbing is not.

## Building it yourself

```sh
curl -O https://dumps.wikimedia.org/jawiki/latest/jawiki-latest-pages-articles-multistream.xml.bz2
bzcat jawiki-latest-pages-articles-multistream.xml.bz2 | \
  dotnet run -c Release --project src/WikiYomitan -- jawiki.zip --date 2026-07-06
```

Takes roughly 15 minutes and ~2 GB of RAM for the full dump (~2.4 M pages).
`--limit N` stops after N pages, for smoke tests.

## Design

- `Wikitext.fs` — wikitext → plain-text lead + optional `Reading`
  (a private single-case DU that can only hold pure kana), plus
  `findTermReading` for mining a mentioned variant's reading out of a lead.
- `Dump.fs` — streams `Article`/`Redirect` pages (a DU: a redirect can never be
  mistaken for an article) out of the dump XML.
- `Yomitan.fs` — streams entries into `term_bank_N.json` files inside the zip.
- Single pass over the dump: article entries are written as they stream by;
  redirect entries are resolved (up to 3 hops) afterwards from an in-memory
  map of article leads.

## Licensing

- **Code**: [MIT](LICENSE).
- **Release artifacts (dictionary zips)**: contain text from
  [Japanese Wikipedia](https://ja.wikipedia.org/) by Wikipedia contributors,
  licensed under
  [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/). Every entry
  links back to its source article, and each release states the dump date.
  If you redistribute or remix a generated dictionary, keep the attribution
  and the CC BY-SA license.

This project is not affiliated with or endorsed by the Wikimedia Foundation.
