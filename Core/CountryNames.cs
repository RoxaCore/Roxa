// Roxa - a VPN client for Windows built on Xray-core
// Copyright (C) 2026 Roxa contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.
//
// Additional terms under section 7 of that license: the "Roxa" name and
// visual identity are not licensed. See "The name and logo are not
// covered" in README.md.

namespace Roxa.Core;

/// <summary>نام کشورها به زبان‌های مختلف رابط — برای نمایش به‌جای کد دوحرفی.</summary>
internal static class CountryNames
{
    // ترتیب زبان‌ها در هر ردیف: fa en ar zh ru tr ur es fr ko de ja pt hi
    private static readonly string[] Langs = ["fa", "en", "ar", "zh", "ru", "tr", "ur", "es", "fr", "ko", "de", "ja", "pt", "hi"];

    public static string? Get(string code, string lang)
    {
        if (string.IsNullOrEmpty(code) || code.Length != 2 || !Map.TryGetValue(code, out var names)) return null;
        int idx = Array.FindIndex(Langs, l => l.Equals(lang, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx < names.Length && names[idx].Length > 0) return names[idx];
        int en = Array.FindIndex(Langs, l => l == "en");
        return names[en];
    }

    private static Dictionary<string, string[]> Build()
    {
        var d = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (Rows is null) return d;
        foreach (string row in Rows)
        {
            string[] p = row.Split('|');
            if (p.Length == 1 + Langs.Length) d[p[0]] = p[1..];
        }
        return d;
    }

    private static readonly string[] Rows =
    [
        "DE|آلمان|Germany|ألمانيا|德国|Германия|Almanya|جرمنی|Alemania|Allemagne|독일|Deutschland|ドイツ|Alemanha|जर्मनी",
        "NL|هلند|Netherlands|هولندا|荷兰|Нидерланды|Hollanda|نیدرلینڈز|Países Bajos|Pays-Bas|네덜란드|Niederlande|オランダ|Holanda|नीदरलैंड",
        "FR|فرانسه|France|فرنسا|法国|Франция|Fransa|فرانسه|Francia|France|프랑스|Frankreich|フランス|França|फ्रांस",
        "GB|انگلیس|United Kingdom|بريطانيا|英国|Великобритания|İngiltere|برطانیہ|Reino Unido|Royaume-Uni|영국|Großbritannien|イギリス|Reino Unido|ब्रिटेन",
        "US|آمریکا|United States|الولايات المتحدة|美国|США|Amerika|امریکا|Estados Unidos|États-Unis|미국|USA|アメリカ|Estados Unidos|संयुक्त राज्य",
        "FI|فنلاند|Finland|فنلاند|芬兰|Финляндия|Finlandiya|فنلینڈ|Finlandia|Finlande|핀란드|Finnland|フィンランド|Finlândia|फिनलैंड",
        "SE|سوئد|Sweden|السويد|瑞典|Швеция|İsveç|سویڈ|Suecia|Suède|스웨덴|Schweden|スウェーデン|Suécia|स्वीडन",
        "NO|نروژ|Norway|النرويج|挪威|Норвегия|Norveç|ناروے|Noruega|Norvège|노르웨이|Norwegen|ノルウェー|Noruega|नॉर्वे",
        "DK|دانمارک|Denmark|الدنمارك|丹麦|Дания|Danimarka|ڈینمارک|Dinamarca|Danemark|덴마크|Dänemark|デンマーク|Dinamarca|डेनमार्क",
        "CH|سوئیس|Switzerland|سويسرا|瑞士|Швейцария|İsviçre|سوئیٹزرلینڈ|Suiza|Suisse|스위스|Schweiz|スイス|Suíça|स्विट्जरलैंड",
        "AT|اتریش|Austria|النمسا|奥地利|Австрия|Avusturya|آسٹریا|Austria|Autriche|오스트리아|Österreich|オーストリア|Áustria|ऑस्ट्रिया",
        "BE|بلژیک|Belgium|بلجيكا|比利时|Бельгия|Belçika|بلجیم|Bélgica|Belgique|벨기에|Belgien|ベルギー|Bélgica|बेल्जियम",
        "IE|ایرلند|Ireland|أيرلندا|爱尔兰|Ирландия|İrlanda|ائرلینڈ|Irlanda|Irlande|아일랜드|Irland|アイルランド|Irlanda|आयरलैंड",
        "IT|ایتالیا|Italy|إيطاليا|意大利|Италия|İtalya|اطالیہ|Italia|Italie|이탈리아|Italien|イタリア|Itália|इटली",
        "ES|اسپانیا|Spain|إسبانيا|西班牙|Испания|İspanya|اسپین|España|Espagne|스페인|Spanien|スペイン|Espanha|स्पेन",
        "PT|پرتغال|Portugal|البرتغال|葡萄牙|Португалия|Portekiz|پرتگال|Portugal|Portugal|포르투갈|Portugal|ポルトガル|Portugal|पुर्तगाल",
        "PL|لهستان|Poland|بولندا|波兰|Польша|Polonya|پولند|Polonia|Pologne|폴란드|Polen|ポーランド|Polônia|पोलैंड",
        "CZ|چک|Czechia|التشيك|捷克|Чехия|Çekya|چیک|Chequia|Tchéquie|체코|Tschechien|チェコ|Tchéquia|चेक",
        "RO|رومانی|Romania|رومانيا|罗马尼亚|Румыния|Romanya|رومانیا|Rumanía|Roumanie|루마니아|Rumänien|ルーマニア|Romênia|रोमानिया",
        "HU|مجارستان|Hungary|المجر|匈牙利|Венгрия|Macaristan|ہنگری|Hungría|Hongrie|헝가리|Ungarn|ハンガリー|Hungria|हंगरी",
        "BG|بلغارستان|Bulgaria|بلغاريا|保加利亚|Болгария|Bulgaristan|بلغاریہ|Bulgaria|Bulgarie|불가리아|Bulgarien|ブルガリア|Bulgária|बुल्गारिया",
        "GR|یونان|Greece|اليونان|希腊|Греция|Yunanistan|یونان|Grecia|Grèce|그리스|Griechenland|ギリシャ|Grécia|ग्रीस",
        "UA|اوکراین|Ukraine|أوكرانيا|乌克兰|Украина|Ukrayna|یوکرین|Ucrania|Ukraine|우크라이나|Ukraine|ウクライナ|Ucrânia|यूक्रेन",
        "BY|بلاروس|Belarus|بيلاروسيا|白俄罗斯|Беларусь|Belarus|بیلاروس|Bielorrusia|Biélorussie|벨라루스|Belarus|ベラルーシ|Bielorrússia|बेलारूस",
        "RU|روسیه|Russia|روسيا|俄罗斯|Россия|Rusya|روسیہ|Rusia|Russie|러시아|Russland|ロシア|Rússia|रूस",
        "TR|ترکیه|Turkey|تركيا|土耳其|Турция|Türkiye|ترکیہ|Turquía|Turquie|튀르키예|Türkei|トルコ|Turquia|तुर्की",
        "AE|امارات|United Arab Emirates|الإمارات|阿联酋|ОАЭ|Birleşik Arap Emirlikleri|متحدہ عرب امارات|Emiratos Árabes Unidos|Émirats arabes unis|아랍에미리트|VAE|アラブ首長国連邦|Emirados Árabes Unidos|संयुक्त अरब अमीरात",
        "SA|عربستان سعودی|Saudi Arabia|السعودية|沙特阿拉伯|Саудовская Аравия|Suudi Arabistan|سعودی عرب|Arabia Saudita|Arabie saoudite|사우디아라비아|Saudi-Arabien|サウジアラビア|Arábia Saudita|सऊदी अरब",
        "IL|اسرائیل|Israel|إسرائيل|以色列|Израиль|İsrail|اسرائیل|Israel|Israël|이스라엘|Israel|イスラエル|Israel|इज़राइल",
        "IR|ایران|Iran|إيران|伊朗|Иран|İran|ایران|Irán|Iran|이란|Iran|イラン|Irã|ईरान",
        "IN|هند|India|الهند|印度|Индия|Hindistan|بھارت|India|Inde|인도|Indien|インド|Índia|भारत",
        "PK|پاکستان|Pakistan|باكستان|巴基斯坦|Пакистан|Pakistan|پاکستان|Pakistán|Pakistan|파키스탄|Pakistan|パキスタン|Paquistão|पाकिस्तान",
        "BD|بنگلادش|Bangladesh|بنغلاديش|孟加拉国|Бангладеш|Bangladeş|بنگلادیش|Bangladés|Bangladesh|방글라데시|Bangladesch|バングラデシュ|Bangladesh|बांग्लादेश",
        "SG|سنگاپور|Singapore|سنغافورة|新加坡|Сингапур|Singapur|سنگاپور|Singapur|Singapour|싱가포르|Singapur|シンガポール|Singapura|सिंगापुर",
        "JP|ژاپن|Japan|اليابان|日本|Япония|Japonya|جاپان|Japón|Japon|일본|Japan|日本|Japão|जापान",
        "KR|کره جنوبی|South Korea|كوريا الجنوبية|韩国|Южная Корея|Güney Kore|جنوبی کوریا|Corea del Sur|Corée du Sud|대한민국|Südkorea|韓国|Coreia do Sul|दक्षिण कोरिया",
        "HK|هنگ کنگ|Hong Kong|هونغ كونغ|香港|Гонконг|Hong Kong|ہانگ کنگ|Hong Kong|Hong Kong|홍콩|Hongkong|香港|Hong Kong|हांगकांग",
        "TW|تایوان|Taiwan|تايوان|台湾|Тайвань|Tayvan|تائیوان|Taiwán|Taïwan|대만|Taiwan|台湾|Taiwan|ताइवान",
        "TH|تایلند|Thailand|تايلاند|泰国|Таиланд|Tayland|تھائی لینڈ|Tailandia|Thaïlande|태국|Thailand|タイランド|Tailândia|थाईलैंड",
        "MY|مالزی|Malaysia|ماليزيا|马来西亚|Малайзия|Malezya|ملائیشیا|Malasia|Malaisie|말레이시아|Malaysia|マレーシア|Malásia|मलेशिया",
        "ID|اندونزی|Indonesia|إندونيسيا|印度尼西亚|Индонезия|Endonezya|انڈونیشیا|Indonesia|Indonésie|인도네시아|Indonesien|インドネシア|Indonésia|इंडोनेशिया",
        "PH|فیلیپین|Philippines|الفلبين|菲律宾|Филиппины|Filipinler|فلپین|Filipinas|Philippines|필리핀|Philippinen|フィリピン|Filipinas|फिलीपींस",
        "VN|ویتنام|Vietnam|فيتنام|越南|Вьетнам|Vietnam|ویتنام|Vietnam|Viêt Nam|베트남|Vietnam|ベトナム|Vietnã|वियतनाम",
        "KZ|قزاقستان|Kazakhstan|كازاخستان|哈萨克斯坦|Казахстан|Kazakistan|قازقستان|Kazajistán|Kazakhstan|카자흐스탄|Kasachstan|カザフスタン|Cazaquistão|कज़ाकिस्तान",
        "CN|چین|China|الصين|中国|Китай|Çin|چین|China|Chine|중국|China|中国|China|चीन",
        "AU|استرالیا|Australia|أستراليا|澳大利亚|Австралия|Avustralya|آسٹریلیا|Australia|Australie|호주|Australien|オーストラリア|Austrália|ऑस्ट्रेलियا",
        "CA|کانادا|Canada|كندا|加拿大|Канада|Kanada|کیندا|Canadá|Canada|캐나다|Kanada|カナダ|Canadá|कनाडा",
        "BR|برزیل|Brazil|البرازيل|巴西|Бразилия|Brezilya|برازیل|Brasil|Brésil|브라질|Brasilien|ブラジル|Brasil|ब्राज़ील",
        "AR|آرژانتین|Argentina|الأرجنتين|阿根廷|Аргентина|Arjantin|ارجنٹین|Argentina|Argentine|아르헨티나|Argentinien|アルゼンチン|Argentina|अर्जेंटीना",
        "CL|شیلی|Chile|شيلي|智利|Чили|Şili|چلی|Chile|Chili|칠레|Chile|チリ|Chile|चिली",
        "MX|مکزیک|Mexico|المكسيك|墨西哥|Мексика|Meksika|میکسیکو|México|Mexique|멕시코|Mexiko|メキシコ|México|मेक्सिको",
        "ZA|آفریقای جنوبی|South Africa|جنوب أفريقيا|南非|Южная Африка|Güney Afrika|جنوبی افریقیہ|Sudáfrica|Afrique du Sud|남아프리카|Südafrika|南アフリカ|África do Sul|दक्षिण अफ्रीका",
        "EG|مصر|Egypt|مصر|埃及|Египет|Mısır|مصر|Egipto|Égypte|이집트|Ägypten|エジプト|Egito|मिस्र",
        "NG|نیجریه|Nigeria|نيجيريا|尼日利亚|Нигерия|Nijerya|نائجیریا|Nigeria|Nigéria|나이지리아|Nigeria|ナイジェリア|Nigéria|नाइजीरिया",
        "KE|کنیا|Kenya|كينيا|肯尼亚|Кения|Kenya|کینیا|Kenia|Kenya|케냐|Kenia|ケニア|Quênia|कीनिया",
        "MD|مولداوی|Moldova|مولدوفا|摩尔多瓦|Молдова|Moldova|Moldova|Moldavia|Moldavie|몰도바|Moldawien|モルドバ|Moldávia|मोल्दोवा",
        "GE|گرجستان|Georgia|جورجيا|格鲁吉亚|Грузия|Gürcistan|جیورجیا|Georgia|Géorgie|조지아|Georgien|ジョージア|Geórgia|जॉर्जियا",
        "RS|صربستان|Serbia|صربيا|塞尔维亚|Сербия|Sırbistan|سربیا|Serbia|Serbie|세르비아|Serbien|セルビア|Sérvia|सर्बिया",
        "HR|کرواسی|Croatia|كرواتيا|克罗地亚|Хорватия|Hırvatistan|کروشیا|Croacia|Croatie|크로아티아|Kroatien|クロアチア|Croácia|क्रोएशिया",
        "LT|لیتوانی|Lithuania|ليتوانيا|立陶宛|Литва|Litanya|لیٹوینیا|Lituania|Lituanie|리투아니아|Litauen|リトアニア|Lituânia|लिथुआनिया",
        "LV|لتونی|Latvia|لاتفيا|拉脱维亚|Латвия|Letonya|لیٹویا|Letonia|Lettonie|라트비아|Lettland|ラトビア|Letônia|लातविया",
        "EE|استونی|Estonia|إستونيا|爱沙尼亚|Эстония|Estonya|اسٹونیا|Estonia|Estonie|에스토니아|Estland|エストニア|Estônia|एस्टोनिया",
        "SK|اسلواکی|Slovakia|سلوفاكيا|斯洛伐克|Словакия|Slovakya|سلوواکیا|Eslovaquia|Slovaquie|슬로바키아|Slowakei|スロバキア|Eslováquia|स्लोवाकिया",
        "SI|اسلوونی|Slovenia|سلوفينيا|斯洛文尼亚|Словения|Slovenya|سلووینیا|Eslovenia|Slovénie|슬로베니아|Slowenien|スロベニア|Eslovênia|स्लोवेनिया",
    ];

    // باید بعد از Rows تعریف شود؛ وگرنه Build() موقع مقداردهی Rows را null می‌بیند.
    private static readonly Dictionary<string, string[]> Map = Build();
}
