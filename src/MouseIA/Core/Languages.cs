using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MouseIA.Core;

public sealed record LanguageInfo(string Code, string English, string Portuguese, string Native)
{
    public string Display(bool portuguese) =>
        (portuguese ? Portuguese : English) + (Native != (portuguese ? Portuguese : English) ? " · " + Native : "");
}

/// <summary>Languages offered by the translator. Codes are BCP-47, used to pick a voice.</summary>
public static class Languages
{
    public static readonly IReadOnlyList<LanguageInfo> All = new List<LanguageInfo>
    {
        new("af", "Afrikaans", "Africâner", "Afrikaans"),
        new("sq", "Albanian", "Albanês", "Shqip"),
        new("de", "German", "Alemão", "Deutsch"),
        new("am", "Amharic", "Amárico", "አማርኛ"),
        new("ar", "Arabic", "Árabe", "العربية"),
        new("hy", "Armenian", "Armênio", "Հայերեն"),
        new("as", "Assamese", "Assamês", "অসমীয়া"),
        new("az", "Azerbaijani", "Azerbaijano", "Azərbaycan"),
        new("eu", "Basque", "Basco", "Euskara"),
        new("be", "Belarusian", "Bielorrusso", "Беларуская"),
        new("bn", "Bengali", "Bengali", "বাংলা"),
        new("bs", "Bosnian", "Bósnio", "Bosanski"),
        new("bg", "Bulgarian", "Búlgaro", "Български"),
        new("my", "Burmese", "Birmanês", "မြန်မာ"),
        new("ca", "Catalan", "Catalão", "Català"),
        new("kk", "Kazakh", "Cazaque", "Қазақ"),
        new("ceb", "Cebuano", "Cebuano", "Cebuano"),
        new("zh-CN", "Chinese (Simplified)", "Chinês (simplificado)", "简体中文"),
        new("zh-TW", "Chinese (Traditional)", "Chinês (tradicional)", "繁體中文"),
        new("si", "Sinhala", "Cingalês", "සිංහල"),
        new("ko", "Korean", "Coreano", "한국어"),
        new("co", "Corsican", "Corso", "Corsu"),
        new("ht", "Haitian Creole", "Crioulo haitiano", "Kreyòl ayisyen"),
        new("hr", "Croatian", "Croata", "Hrvatski"),
        new("ku", "Kurdish", "Curdo", "Kurdî"),
        new("da", "Danish", "Dinamarquês", "Dansk"),
        new("sk", "Slovak", "Eslovaco", "Slovenčina"),
        new("sl", "Slovenian", "Esloveno", "Slovenščina"),
        new("es", "Spanish", "Espanhol", "Español"),
        new("eo", "Esperanto", "Esperanto", "Esperanto"),
        new("et", "Estonian", "Estoniano", "Eesti"),
        new("fil", "Filipino", "Filipino", "Filipino"),
        new("fi", "Finnish", "Finlandês", "Suomi"),
        new("fr", "French", "Francês", "Français"),
        new("fy", "Frisian", "Frísio", "Frysk"),
        new("gd", "Scottish Gaelic", "Gaélico escocês", "Gàidhlig"),
        new("gl", "Galician", "Galego", "Galego"),
        new("cy", "Welsh", "Galês", "Cymraeg"),
        new("ka", "Georgian", "Georgiano", "ქართული"),
        new("el", "Greek", "Grego", "Ελληνικά"),
        new("gn", "Guarani", "Guarani", "Avañe'ẽ"),
        new("gu", "Gujarati", "Guzerate", "ગુજરાતી"),
        new("ha", "Hausa", "Hauçá", "Hausa"),
        new("haw", "Hawaiian", "Havaiano", "ʻŌlelo Hawaiʻi"),
        new("he", "Hebrew", "Hebraico", "עברית"),
        new("hi", "Hindi", "Hindi", "हिन्दी"),
        new("hmn", "Hmong", "Hmong", "Hmoob"),
        new("nl", "Dutch", "Holandês", "Nederlands"),
        new("hu", "Hungarian", "Húngaro", "Magyar"),
        new("ig", "Igbo", "Igbo", "Igbo"),
        new("yi", "Yiddish", "Iídiche", "ייִדיש"),
        new("id", "Indonesian", "Indonésio", "Bahasa Indonesia"),
        new("en", "English", "Inglês", "English"),
        new("yo", "Yoruba", "Iorubá", "Yorùbá"),
        new("ga", "Irish", "Irlandês", "Gaeilge"),
        new("is", "Icelandic", "Islandês", "Íslenska"),
        new("it", "Italian", "Italiano", "Italiano"),
        new("ja", "Japanese", "Japonês", "日本語"),
        new("jv", "Javanese", "Javanês", "Basa Jawa"),
        new("km", "Khmer", "Khmer", "ខ្មែរ"),
        new("kn", "Kannada", "Canarês", "ಕನ್ನಡ"),
        new("rw", "Kinyarwanda", "Quiniaruanda", "Kinyarwanda"),
        new("ky", "Kyrgyz", "Quirguiz", "Кыргызча"),
        new("lo", "Lao", "Laosiano", "ລາວ"),
        new("la", "Latin", "Latim", "Latina"),
        new("lv", "Latvian", "Letão", "Latviešu"),
        new("ln", "Lingala", "Lingala", "Lingála"),
        new("lt", "Lithuanian", "Lituano", "Lietuvių"),
        new("lb", "Luxembourgish", "Luxemburguês", "Lëtzebuergesch"),
        new("mk", "Macedonian", "Macedônio", "Македонски"),
        new("ms", "Malay", "Malaio", "Bahasa Melayu"),
        new("ml", "Malayalam", "Malaiala", "മലയാളം"),
        new("mg", "Malagasy", "Malgaxe", "Malagasy"),
        new("mt", "Maltese", "Maltês", "Malti"),
        new("mi", "Maori", "Maori", "Te Reo Māori"),
        new("mr", "Marathi", "Marati", "मराठी"),
        new("mn", "Mongolian", "Mongol", "Монгол"),
        new("ne", "Nepali", "Nepalês", "नेपाली"),
        new("nb", "Norwegian", "Norueguês", "Norsk"),
        new("ny", "Chichewa", "Nianja", "Chichewa"),
        new("or", "Odia", "Oriá", "ଓଡ଼ିଆ"),
        new("ps", "Pashto", "Pashto", "پښتو"),
        new("fa", "Persian", "Persa", "فارسی"),
        new("pl", "Polish", "Polonês", "Polski"),
        new("pt-BR", "Portuguese (Brazil)", "Português (Brasil)", "Português (Brasil)"),
        new("pt-PT", "Portuguese (Portugal)", "Português (Portugal)", "Português (Portugal)"),
        new("pa", "Punjabi", "Punjabi", "ਪੰਜਾਬੀ"),
        new("qu", "Quechua", "Quíchua", "Runa Simi"),
        new("ro", "Romanian", "Romeno", "Română"),
        new("ru", "Russian", "Russo", "Русский"),
        new("sm", "Samoan", "Samoano", "Gagana Sāmoa"),
        new("sr", "Serbian", "Sérvio", "Српски"),
        new("st", "Sesotho", "Sesoto", "Sesotho"),
        new("sn", "Shona", "Shona", "chiShona"),
        new("sd", "Sindhi", "Sindi", "سنڌي"),
        new("so", "Somali", "Somali", "Soomaali"),
        new("sw", "Swahili", "Suaíli", "Kiswahili"),
        new("sv", "Swedish", "Sueco", "Svenska"),
        new("su", "Sundanese", "Sundanês", "Basa Sunda"),
        new("tg", "Tajik", "Tadjique", "Тоҷикӣ"),
        new("th", "Thai", "Tailandês", "ไทย"),
        new("ta", "Tamil", "Tâmil", "தமிழ்"),
        new("tt", "Tatar", "Tártaro", "Татар"),
        new("cs", "Czech", "Tcheco", "Čeština"),
        new("te", "Telugu", "Telugo", "తెలుగు"),
        new("tr", "Turkish", "Turco", "Türkçe"),
        new("tk", "Turkmen", "Turcomeno", "Türkmen"),
        new("uk", "Ukrainian", "Ucraniano", "Українська"),
        new("ug", "Uyghur", "Uigur", "ئۇيغۇرچە"),
        new("ur", "Urdu", "Urdu", "اردو"),
        new("uz", "Uzbek", "Uzbeque", "Oʻzbek"),
        new("vi", "Vietnamese", "Vietnamita", "Tiếng Việt"),
        new("xh", "Xhosa", "Xhosa", "isiXhosa"),
        new("zu", "Zulu", "Zulu", "isiZulu"),
    };

    public static LanguageInfo? Find(string code) =>
        All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>The language that matches a culture such as pt-BR or en-US.</summary>
    public static LanguageInfo FromCulture(CultureInfo culture)
    {
        var name = culture.Name;
        var exact = Find(name);
        if (exact != null) return exact;
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return Find(name.Contains("TW") || name.Contains("HK") || name.Contains("Hant") ? "zh-TW" : "zh-CN")!;
        if (name.StartsWith("pt", StringComparison.OrdinalIgnoreCase)) return Find(name.EndsWith("PT") ? "pt-PT" : "pt-BR")!;
        if (name.StartsWith("no", StringComparison.OrdinalIgnoreCase) || name.StartsWith("nn", StringComparison.OrdinalIgnoreCase))
            return Find("nb")!;
        return Find(culture.TwoLetterISOLanguageName) ?? Find("en")!;
    }

    /// <summary>Resolves "auto" to the Windows display language.</summary>
    public static LanguageInfo Resolve(string code, CultureInfo uiCulture) =>
        code == "auto" || string.IsNullOrWhiteSpace(code) ? FromCulture(uiCulture) : Find(code) ?? FromCulture(uiCulture);
}
