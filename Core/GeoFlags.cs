using System.Text;
using System.Text.RegularExpressions;

namespace XrayClient.Core;

/// <summary>تشخیص کشور از روی اسم/آدرس + ایموجی پرچم.</summary>
public static class GeoFlags
{
    private static readonly HashSet<string> IsoCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AD","AE","AF","AG","AI","AL","AM","AO","AQ","AR","AS","AT","AU","AW","AX","AZ",
        "BA","BB","BD","BE","BF","BG","BH","BI","BJ","BL","BM","BN","BO","BQ","BR","BS","BT","BV","BW","BY","BZ",
        "CA","CC","CD","CF","CG","CH","CI","CK","CL","CM","CN","CO","CR","CU","CV","CW","CX","CY","CZ",
        "DE","DJ","DK","DM","DO","DZ","EC","EE","EG","EH","ER","ES","ET","FI","FJ","FK","FM","FO","FR",
        "GA","GB","GD","GE","GF","GG","GH","GI","GL","GM","GN","GP","GQ","GR","GS","GT","GU","GW","GY",
        "HK","HM","HN","HR","HT","HU","ID","IE","IL","IM","IN","IO","IQ","IR","IS","IT","JE","JM","JO","JP",
        "KE","KG","KH","KI","KM","KN","KP","KR","KW","KY","KZ","LA","LB","LC","LI","LK","LR","LS","LT","LU","LV","LY",
        "MA","MC","MD","ME","MF","MG","MH","MK","ML","MM","MN","MO","MP","MQ","MR","MS","MT","MU","MV","MW","MX","MY","MZ",
        "NA","NC","NE","NF","NG","NI","NL","NO","NP","NR","NU","NZ","OM","PA","PE","PF","PG","PH","PK","PL","PM","PN","PR","PS","PT","PW","PY",
        "QA","RE","RO","RS","RU","RW","SA","SB","SC","SD","SE","SG","SH","SI","SJ","SK","SL","SM","SN","SO","SR","SS","ST","SV","SX","SY","SZ",
        "TC","TD","TF","TG","TH","TJ","TK","TL","TM","TN","TO","TR","TT","TV","TW","TZ","UA","UG","UM","US","UY","UZ",
        "VA","VC","VE","VG","VI","VN","VU","WF","WS","YE","YT","ZA","ZM","ZW"
    };

    public static string DetectCountry(string name, string address)
    {
        string combined = $"{name} {address}";
        string fromFlag = FlagToCode(combined);
        if (!string.IsNullOrEmpty(fromFlag)) return fromFlag;

        foreach (string token in combined.Split(new[] { ' ', '·', '|', ',', '_', '-', '.', ':', '(', ')' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Regex.Replace(token, "[^A-Za-z]", "").ToUpperInvariant();
            if (candidate.Length == 2 && IsoCodes.Contains(candidate)) return candidate;
        }
        return "";
    }

    public static string FlagEmoji(string code)
    {
        if (string.IsNullOrEmpty(code) || code.Length != 2 || !IsoCodes.Contains(code)) return "🌐";
        const int regionalA = 0x1F1E6;
        char c1 = (char)(regionalA + (char.ToUpperInvariant(code[0]) - 'A'));
        char c2 = (char)(regionalA + (char.ToUpperInvariant(code[1]) - 'A'));
        return new string(new[] { c1, c2 });
    }

    private static string FlagToCode(string text)
    {
        var sb = new StringBuilder(2);
        int i = 0;
        while (i < text.Length)
        {
            int cp = char.ConvertToUtf32(text, i);
            if (cp is >= 0x1F1E6 and <= 0x1F1FF)
                sb.Append((char)('A' + (cp - 0x1F1E6)));
            i += char.IsSurrogatePair(text, i) ? 2 : 1;
        }
        return sb.Length == 2 ? sb.ToString() : "";
    }
}
