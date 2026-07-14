module WikiYomitan.Tests

open System.IO
open Xunit
open WikiYomitan

let private fixture name =
    File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "fixtures", name + ".wiki"))

// ---- Lead extraction on real articles ----

[<Fact>]
let ``周溝: lead paragraph is extracted past file links and templates`` () =
    let lead = (Wikitext.extractLead (fixture "周溝")).Value
    Assert.StartsWith("周溝（しゅうこう", lead.Gloss)
    Assert.Contains("古墳", lead.Gloss)
    // no wikitext markup survives
    Assert.DoesNotContain("[[", lead.Gloss)
    Assert.DoesNotContain("{{", lead.Gloss)
    Assert.DoesNotContain("'''", lead.Gloss)
    Assert.DoesNotContain("thumb", lead.Gloss)

[<Fact>]
let ``周溝: reading is しゅうこう`` () =
    let lead = (Wikitext.extractLead (fixture "周溝")).Value
    Assert.Equal("しゅうこう", lead.Reading.Value.Value)

[<Fact>]
let ``東京都: reading is とうきょうと despite a huge infobox`` () =
    let lead = (Wikitext.extractLead (fixture "東京都")).Value
    Assert.StartsWith("東京都", lead.Gloss)
    Assert.Equal("とうきょうと", lead.Reading.Value.Value)

[<Fact>]
let ``コンピュータ: katakana title still yields a lead`` () =
    let lead = (Wikitext.extractLead (fixture "コンピュータ")).Value
    Assert.StartsWith("コンピュータ", lead.Gloss)

[<Fact>]
let ``大仙陵古墳: gloss is truncated to a sentence boundary`` () =
    let lead = (Wikitext.extractLead (fixture "大仙陵古墳")).Value
    Assert.True(lead.Gloss.Length <= 501)
    Assert.EndsWith("。", lead.Gloss)

[<Fact>]
let ``empty and markup-only pages produce no lead`` () =
    Assert.True((Wikitext.extractLead "").IsNone)
    Assert.True((Wikitext.extractLead "{{工事中}}\n[[Category:何か]]").IsNone)

[<Fact>]
let ``imagemap content and empty-paren husks are removed`` () =
    let wikitext =
        "<imagemap>File:montage.png|thumb|420px|'''上段''': 何かの写真。</imagemap>\n"
        + "'''ポワティエの戦い'''（{{lang-fr|Bataille de Poitiers}}、{{lang-en|Battle of Poitiers}}）は、百年戦争の戦いである。"

    let lead = (Wikitext.extractLead wikitext).Value
    Assert.Equal("ポワティエの戦いは、百年戦争の戦いである。", lead.Gloss)

[<Fact>]
let ``nested paren husks and magic words are removed`` () =
    let wikitext =
        "__目次強制__\n'''ヘーパイストス'''（{{lang-grc-short|'''ΗΦΑΙΣΤΟΣ'''}}（{{lang|grc|Ἥφαιστος}}）、{{lang-*-Latn|grc|Hḗphaistos}}）は、ギリシア神話に登場する神である。"

    let lead = (Wikitext.extractLead wikitext).Value
    Assert.Equal("ヘーパイストスは、ギリシア神話に登場する神である。", lead.Gloss)

[<Fact>]
let ``file links with a leading space are still dropped, caption and all`` () =
    let wikitext =
        "'''美少女'''（びしょうじょ）は、少女を指す。[[ File:Alice.jpg |thumb|250px|right|[[画家]]「アリス王女の絵姿」(1859年)]]"

    let lead = (Wikitext.extractLead wikitext).Value
    Assert.Equal("美少女（びしょうじょ）は、少女を指す。", lead.Gloss)

// ---- Reading candidates ----

[<Fact>]
let ``readings must be pure kana`` () =
    Assert.True((Wikitext.Reading.tryCreate "しゅうこう").IsSome)
    Assert.True((Wikitext.Reading.tryCreate "トーキョー").IsSome)
    Assert.True((Wikitext.Reading.tryCreate "英: Tokyo").IsNone)
    Assert.True((Wikitext.Reading.tryCreate "1867年").IsNone)
    Assert.True((Wikitext.Reading.tryCreate "").IsNone)

[<Fact>]
let ``a lead can give the reading of a mentioned variant term`` () =
    let gloss =
        "周溝（しゅうこう）は、日本の考古学における学術用語の1つ。周濠（しゅうごう）・周壕（しゅうごう）とする場合もある。"

    Assert.Equal("しゅうごう", (Wikitext.findTermReading "周濠" gloss).Value.Value)
    // the term itself resolves to its own reading, not the variant's
    Assert.Equal("しゅうこう", (Wikitext.findTermReading "周溝" gloss).Value.Value)
    // mentioned without a reading → none
    Assert.True((Wikitext.findTermReading "本尊" gloss).IsNone)
    // mention followed by non-reading parens is skipped, later mention wins
    Assert.Equal(
        "しゅうごう",
        (Wikitext.findTermReading "周濠" "周濠（英: moat）も参照。周濠（しゅうごう）とも。").Value.Value
    )

[<Fact>]
let ``a term embedded at the end of a longer word does not steal its reading`` () =
    let gloss = "日本中央競馬会（にっぽんちゅうおうけいばかい、JRA）は、競馬法に基づく特殊法人。"
    Assert.True((Wikitext.findTermReading "中央競馬会" gloss).IsNone)
    // katakana suffix inside a katakana word is likewise rejected
    Assert.True((Wikitext.findTermReading "ワーク" "ハローワーク（はろーわーく）とは。").IsNone)
    // but a particle before the term is a word boundary, so this still works
    Assert.Equal(
        "にっしょうき",
        (Wikitext.findTermReading "日章旗" "法律上は日章旗（にっしょうき）と呼ばれる。").Value.Value
    )

[<Fact>]
let ``compound and latin embeddings do not steal readings either`` () =
    // one half of a ・-joined compound title
    Assert.True((Wikitext.findTermReading "青い州" "赤い州・青い州（あかいしゅう・あおいしゅう）とは。").IsNone)
    // latin word inside a longer latin name
    Assert.True((Wikitext.findTermReading "Home" "PlayStation Home（プレイステーション ホーム）は。").IsNone)

[<Fact>]
let ``enumerated variants each keep their own reading despite the ・ between them`` () =
    let gloss = "周溝（しゅうこう）は溝。周堀（しゅうごう）・周濠（しゅうごう）・周壕（しゅうごう）とする場合もある。"
    Assert.Equal("しゅうごう", (Wikitext.findTermReading "周濠" gloss).Value.Value)
    Assert.Equal("しゅうごう", (Wikitext.findTermReading "周壕" gloss).Value.Value)

[<Fact>]
let ``latin terms keep ・-joined readings whole`` () =
    Assert.Equal(
        "サッポロビールオトアジート",
        (Wikitext.findTermReading "SAPPORO BEER OTOAJITO" "SAPPORO BEER OTOAJITO（サッポロビール・オトアジート）は店。").Value.Value
    )

[<Fact>]
let ``name-convention spaces and katakana boundaries also embed`` () =
    // 「姓 名（よみ）」: the given name alone must not take the full reading
    Assert.True((Wikitext.findTermReading "若冲" "伊藤 若冲（いとう じゃくちゅう、1716年 - 1800年）は画家。").IsNone)
    // katakana continuing into a kanji term is still one compound name
    Assert.True(
        (Wikitext.findTermReading "北遠本線" "浜松市自主運行バス北遠本線（はままつしじしゅうんこうバスほくえんほんせん）は路線。").IsNone
    )

[<Fact>]
let ``・ in parens separates alternate readings unless the term itself has ・`` () =
    Assert.Equal(
        "とだちゅうがっこう",
        (Wikitext.findTermReading "戸田中学校" "戸田中学校（とだちゅうがっこう・へだちゅうがっこう）は学校。").Value.Value
    )

    Assert.Equal(
        "おおつかさいかちどいせき",
        (Wikitext.findTermReading "大塚・歳勝土遺跡" "大塚・歳勝土遺跡（おおつか・さいかちどいせき）は遺跡。").Value.Value
    )

// ---- Dump page classification ----

let private dumpXml =
    """<mediawiki xmlns="http://www.mediawiki.org/xml/export-0.11/">
  <page><title>周溝</title><ns>0</ns><id>1</id>
    <revision><id>10</id><text>'''周溝'''（しゅうこう）は、古墳などの周囲に掘られた溝。周囲を巡る。</text></revision></page>
  <page><title>周濠</title><ns>0</ns><id>2</id><redirect title="周溝" />
    <revision><id>11</id><text>#リダイレクト[[周溝]]</text></revision></page>
  <page><title>Wikipedia:何か</title><ns>4</ns><id>3</id>
    <revision><id>12</id><text>プロジェクトページ</text></revision></page>
</mediawiki>"""

[<Fact>]
let ``dump reader classifies articles and redirects and skips other namespaces`` () =
    use stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes dumpXml)
    let pages = Dump.readPages stream |> List.ofSeq

    Assert.Equal<Dump.Page list>(
        [ Dump.Article("周溝", "'''周溝'''（しゅうこう）は、古墳などの周囲に掘られた溝。周囲を巡る。")
          Dump.Redirect("周濠", "周溝") ],
        pages
    )

// ---- Zip output ----

[<Fact>]
let ``writer produces an importable zip with article and redirect rows`` () =
    let path = Path.Combine(Path.GetTempPath(), $"jawiki-test-{System.Guid.NewGuid()}.zip")

    let entries =
        [ { Yomitan.Term = "周溝"
            Yomitan.Reading = "しゅうこう"
            Yomitan.ArticleTitle = "周溝"
            Yomitan.Kind = Yomitan.ArticleEntry "周溝（しゅうこう）は、古墳などの周囲に掘られた溝。" }
          { Yomitan.Term = "周濠"
            Yomitan.Reading = ""
            Yomitan.ArticleTitle = "周濠"
            Yomitan.Kind = Yomitan.RedirectEntry("周溝", "周溝（しゅうこう）は…") } ]

    try
        let count = Yomitan.write path "2026-07-06" 25_000 entries
        Assert.Equal(2, count)

        use zip = System.IO.Compression.ZipFile.OpenRead path
        let names = zip.Entries |> Seq.map (fun e -> e.Name) |> Set.ofSeq
        Assert.Equal<Set<string>>(Set [ "term_bank_1.json"; "index.json" ], names)

        use bankStream = zip.GetEntry("term_bank_1.json").Open()
        let bank = System.Text.Json.JsonDocument.Parse bankStream
        let rows = bank.RootElement.EnumerateArray() |> Array.ofSeq
        Assert.Equal(2, rows.Length)

        let row = rows.[0]
        Assert.Equal("周溝", row.[0].GetString())
        Assert.Equal("しゅうこう", row.[1].GetString())
        Assert.Equal(1, row.[6].GetInt32())
        Assert.Equal("structured-content", row.[5].[0].GetProperty("type").GetString())

        let redirectRow = rows.[1]
        Assert.Equal("周濠", redirectRow.[0].GetString())
        let json = redirectRow.[5].[0].ToString()
        Assert.Contains("周溝", json)
        Assert.Contains("→", json)

        use indexStream = zip.GetEntry("index.json").Open()
        let index = System.Text.Json.JsonDocument.Parse indexStream
        Assert.Equal(3, index.RootElement.GetProperty("format").GetInt32())
        Assert.Contains("2026-07-06", index.RootElement.GetProperty("title").GetString())
    finally
        File.Delete path
