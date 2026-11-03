using SCU.Infrastructure.Windows.Tweaks;
using static SCU.Infrastructure.Windows.Tweaks.KeyboardLayoutTranslator;
using Xunit;

namespace SCU.Tests;

// Unit-тесты для чистой логики перевода раскладки (без Win32 API и хука).
// Покрывают критерии приёмки из аудита: корректные слова не меняются,
// ошибочные раскладки исправляются только при уверенном языковом преимуществе,
// неопределённые токены не трогаются, ё/Ё не приводит к смешанной раскладке.
public sealed class KeyboardLayoutTranslatorTests
{
    private const string Privet = "\u043f\u0440\u0438\u0432\u0435\u0442"; // привет
    private const string Hello = "hello";
    private const string Ghbdtn = "ghbdtn";                            // «привет» на английской раскладке
    private const string Ruddsch = "\u0440\u0443\u0434\u0434\u0449";    // «hello» на русской раскладке

    // ----- транслитерация латиница -> кириллица -----

    [Theory]
    [InlineData("ghbdtn", "привет")]
    [InlineData("hello", "руддщ")]
    [InlineData("Ghbdtn", "Привет")]
    [InlineData("GHBDTN", "ПРИВЕТ")]
    [InlineData("x@#", "ч\"№")]
    [InlineData("a!%*()_+=-", "ф!%*()_+=-")]
    public void LatinToCyrillic_UsesKeyboardLayoutMapping(string input, string expected)
    {
        Assert.Equal(expected, TransliterateLatinToCyrillic(input));
    }

    [Fact]
    public void LatinToCyrillic_UnmappedCharacters_PassedThrough()
    {
        Assert.Equal(Ruddsch + "!", TransliterateLatinToCyrillic(Hello + "!"));
    }

    // ----- транслитерация кириллица -> латиница -----

    [Theory]
    [InlineData("руддщ", "hello")]
    [InlineData("РУДДЩ", "HELLO")]
    [InlineData("Привет", "Ghbdtn")]
    [InlineData("ЁЖЭХЪБЮ", "~:\"{}<>")]
    public void CyrillicToLatin_UsesKeyboardLayoutMapping(string input, string expected)
    {
        Assert.Equal(expected, TransliterateCyrillicToLatin(input));
    }

    [Fact]
    public void CyrillicToLatin_UnmappedCharacters_PassedThrough()
    {
        Assert.Equal(Ghbdtn + "!", TransliterateCyrillicToLatin(Privet + "!"));
    }

    // ----- ё / Ё: передаются как есть транслитерацией, но слова с ними не корректируются -----

    [Fact]
    public void LatinToCyrillic_Yo_PassedThrough()
    {
        Assert.Equal("\u0451", TransliterateLatinToCyrillic("\u0451")); // ё
    }

    [Fact]
    public void CyrillicToLatin_Yo_PassedThrough()
    {
        Assert.Equal("\u0451", TransliterateCyrillicToLatin("\u0451")); // ё
    }

    [Fact]
    public void IsCorrectableWord_WordWithYo_ReturnsFalse()
    {
        Assert.False(IsCorrectableWord(Ruddsch + "\u0451")); // руддщё
        Assert.False(IsCorrectableWord("\u0401" + "bc"));    // Ёbc
    }

    // ----- классификация символов -----

    [Theory]
    [InlineData('a')]
    [InlineData('z')]
    [InlineData('A')]
    [InlineData('Z')]
    public void IsLatin_LatinLetters_ReturnsTrue(char c)
    {
        Assert.True(IsLatin(c));
    }

    [Theory]
    [InlineData('\u0430')] // а
    [InlineData('\u044f')] // я
    [InlineData('\u0410')] // А
    [InlineData('\u042f')] // Я
    [InlineData('\u0451')] // ё
    [InlineData('\u0401')] // Ё
    public void IsCyrillic_CyrillicLetters_ReturnsTrue(char c)
    {
        Assert.True(IsCyrillic(c));
    }

    [Theory]
    [InlineData(' ')]
    [InlineData('.')]
    [InlineData('1')]
    [InlineData('@')]
    public void IsLatin_IsCyrillic_NonLetters_ReturnsFalse(char c)
    {
        Assert.False(IsLatin(c));
        Assert.False(IsCyrillic(c));
    }

    [Fact]
    public void IsAllLatin_RecognizesOnlyLatinLetters()
    {
        Assert.True(IsAllLatin("Hello"));
        Assert.False(IsAllLatin("hello1"));
        Assert.False(IsAllLatin("HelloПривет"));
        Assert.False(IsAllLatin(string.Empty));
    }

    [Fact]
    public void IsAllCyrillic_RecognizesOnlyCyrillicLetters()
    {
        Assert.True(IsAllCyrillic("Привет"));
        Assert.False(IsAllCyrillic("привет1"));
        Assert.False(IsAllCyrillic("helloПривет"));
        Assert.False(IsAllCyrillic(string.Empty));
    }

    // ----- IsCorrectableWord: только одноязычные буквенные слова длиной >= 2, без ё/Ё -----

    [Theory]
    [InlineData("\u043f\u0440\u0438\u0432\u0435\u0442")] // привет
    [InlineData("hello")]
    [InlineData("ghbdtn")]
    [InlineData("\u0440\u0443\u0434\u0434\u0449")]       // руддщ
    [InlineData("ab")] // ровно минимальная длина
    public void IsCorrectableWord_SingleScriptWord_ReturnsTrue(string text)
    {
        Assert.True(IsCorrectableWord(text));
    }

    [Theory]
    [InlineData("")]                                    // пусто
    [InlineData("a")]                                   // слишком короткое (< 2)
    [InlineData("hel\u043b\u043e")]                     // helло: смешанная раскладка
    [InlineData("\u043f\u0440\u0438w\u0435\u0442")]     // приwет: смешанная раскладка
    [InlineData("HelloПривет")]                         // смешанная раскладка
    [InlineData("hello123")]                            // цифры
    [InlineData("foo1")]                                // цифры
    [InlineData("\u043f\u0440\u0438\u0432\u0435\u0442!")] // привет!: пунктуация внутри
    [InlineData("hello!")]                              // пунктуация
    [InlineData("\u0440\u0443\u0434\u0434\u0449\u0451")] // руддщё: есть ё
    [InlineData("ёж")]
    [InlineData("ЁЖ")]
    public void IsCorrectableWord_MixedOrSpecial_ReturnsFalse(string text)
    {
        Assert.False(IsCorrectableWord(text));
    }

    // ----- IsExcludableToken: URL, e-mail, пути, аббревиатуры -----

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://example.com/path?q=value")]
    [InlineData("http://example.org/path")]
    [InlineData("www.example.com")]
    [InlineData("example.com")]
    [InlineData("ghbdtn.ru")]
    [InlineData("www.ghbdtn.ru")]
    [InlineData("sub-domain.example.co.uk")]
    [InlineData("user@mail.ru")]
    [InlineData("user@ghbdtn.com")]
    [InlineData("name+tag@example.co.uk")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData("C:/Windows/System32")]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData("/usr/local/bin")]
    [InlineData("localhost")]
    [InlineData("index.html")]
    [InlineData("HTML")]
    [InlineData("CSS")]
    [InlineData("NASA")]
    [InlineData("HTML/CSS")]
    [InlineData("camelCase")]
    [InlineData("HTMLElement")]
    [InlineData("HTTPServer")]
    [InlineData("XMLParser")]
    [InlineData("parseJSONValue")]
    public void IsExcludableToken_RejectsUrlsEmailsPathsAndAbbreviations(string token)
    {
        Assert.True(IsExcludableToken(token));
    }

    [Theory]
    [InlineData(0x41, true)]   // A-Z
    [InlineData(0x39, true)]   // цифра верхнего ряда
    [InlineData(0xBD, true)]   // дефис / подчёркивание
    [InlineData(0x20, false)]  // пробел
    [InlineData(0x0D, false)]  // Enter
    public void IsPotentialTokenContinuationKey_RecognizesDomainAndFilenameParts(int virtualKey, bool expected)
    {
        Assert.Equal(expected, IsPotentialTokenContinuationKey((uint)virtualKey));
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("привет")]
    [InlineData("C#")]
    [InlineData("qwerty")]
    public void IsExcludableToken_DoesNotRejectOrdinaryText(string token)
    {
        Assert.False(IsExcludableToken(token));
    }

    [Fact]
    public void DictionaryAssets_LoadLargeValidatedDictionaries()
    {
        Assert.True(GetDictionaryWordCount(WordLanguage.English) >= 50_000);
        Assert.True(GetDictionaryWordCount(WordLanguage.Russian) >= 10_000);
        Assert.True(IsKnownWord("programming", WordLanguage.English));
        Assert.True(IsKnownWord("написал", WordLanguage.Russian));
    }

    [Theory]
    [InlineData("!", "!")]
    [InlineData("@", "\"")]
    [InlineData("#", "№")]
    [InlineData("$", ";")]
    [InlineData("^", ":")]
    [InlineData("&", "?")]
    [InlineData("~", "Ё")]
    [InlineData("`", "ё")]
    public void LatinToCyrillic_ShiftedKeyboardSymbols(string input, string expected)
    {
        Assert.Equal(expected, TransliterateLatinToCyrillic(input));
    }

    // ----- DetectWordLanguage: локальные словари, без сети -----

    [Theory]
    [InlineData("hello", WordLanguage.English)]
    [InlineData("world", WordLanguage.English)]
    [InlineData("\u043f\u0440\u0438\u0432\u0435\u0442", WordLanguage.Russian)] // привет
    [InlineData("\u043c\u0438\u0440", WordLanguage.Russian)]                   // мир
    public void DetectWordLanguage_KnownWord_ReturnsLanguage(string text, WordLanguage expected)
    {
        Assert.Equal(expected, DetectWordLanguage(text));
    }

    [Theory]
    [InlineData("ghbdtn")]
    [InlineData("\u0440\u0443\u0434\u0434\u0449")]   // руддщ
    [InlineData("hel\u043b\u043e")]                   // смешанная раскладка
    [InlineData("HelloПривет")]                       // смешанная раскладка
    [InlineData("notawordxyz")]
    [InlineData("")]
    public void DetectWordLanguage_UnknownOrMixed_ReturnsNone(string text)
    {
        Assert.Equal(WordLanguage.None, DetectWordLanguage(text));
    }

    // ----- IsCorrectionDesirable -----

    [Fact]
    public void IsCorrectionDesirable_CorrectsCanonicalWrongLayoutExamples()
    {
        Assert.True(IsCorrectionDesirable(Ghbdtn, Privet, WordLanguage.English));
        Assert.True(IsCorrectionDesirable(Ruddsch, Hello, WordLanguage.Russian));
    }

    [Fact]
    public void IsCorrectionDesirable_CorrectRussianNotCorrected_RussianLayout_ReturnsFalse()
    {
        // Исходник уже корректное русское слово — не переписываем.
        Assert.False(IsCorrectionDesirable(Privet, Ghbdtn, WordLanguage.Russian));
    }

    [Fact]
    public void IsCorrectionDesirable_CorrectEnglishNotCorrected_EnglishLayout_ReturnsFalse()
    {
        // Исходник уже корректное английское слово — не переписываем.
        Assert.False(IsCorrectionDesirable(Hello, Ruddsch, WordLanguage.English));
    }

    [Fact]
    public void IsCorrectionDesirable_CandidateNotKnown_ReturnsFalse()
    {
        // Целевой вариант не является достоверным словом целевого языка.
        Assert.False(IsCorrectionDesirable(Ghbdtn, "qwertyuiop", WordLanguage.English));
        Assert.False(IsCorrectionDesirable(Ruddsch, "\u0439\u0446\u0443\u043a\u0435\u043d\u0433", WordLanguage.Russian));
    }

    [Fact]
    public void IsCorrectionDesirable_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(IsCorrectionDesirable("", Privet, WordLanguage.English));
        Assert.False(IsCorrectionDesirable(Ghbdtn, "", WordLanguage.English));
    }

    [Fact]
    public void IsCorrectionDesirable_RejectsUnsupportedLanguage()
    {
        Assert.False(IsCorrectionDesirable(Ghbdtn, Privet, WordLanguage.None));
    }

    [Fact]
    public void IsCorrectionDesirable_RecognizesExpandedDictionaryExample()
    {
        // После расширения ru.txt слово «написал» распознаётся по словарю,
        // поэтому этот тест проверяет пример автокоррекции, а не биграммный fallback.
        Assert.True(IsCorrectionDesirable("yfgbcfk", "\u043d\u0430\u043f\u0438\u0441\u0430\u043b", WordLanguage.English)); // написал
    }

    // ----- приёмка аудита: корректные и ошибочные слова -----

    [Fact]
    public void Acceptance_RussianTextInRussianLayout_NotCorrected()
    {
        Assert.Equal(WordLanguage.Russian, DetectWordLanguage(Privet));
        Assert.False(IsCorrectionDesirable(Privet, Ghbdtn, WordLanguage.Russian));
    }

    [Fact]
    public void Acceptance_EnglishTextInEnglishLayout_NotCorrected()
    {
        Assert.Equal(WordLanguage.English, DetectWordLanguage(Hello));
        Assert.False(IsCorrectionDesirable(Hello, Ruddsch, WordLanguage.English));
    }

    [Fact]
    public void Acceptance_GhbdtnMapsToPrivet()
    {
        // «ghbdtn» — это «привет», напечатанное на английской раскладке.
        Assert.Equal(Privet, TransliterateLatinToCyrillic(Ghbdtn));
        Assert.Equal(WordLanguage.None, DetectWordLanguage(Ghbdtn));
        Assert.True(IsCorrectionDesirable(Ghbdtn, Privet, WordLanguage.English));
    }

    [Fact]
    public void Acceptance_RuddschMapsToHello()
    {
        // «руддщ» — это «hello», напечатанное на русской раскладке.
        Assert.Equal(Hello, TransliterateCyrillicToLatin(Ruddsch));
        Assert.Equal(WordLanguage.None, DetectWordLanguage(Ruddsch));
        Assert.True(IsCorrectionDesirable(Ruddsch, Hello, WordLanguage.Russian));
    }

    [Fact]
    public void Acceptance_ResultIsNotMixedScript()
    {
        // Результат транслитерации целиком в одном алфавите.
        Assert.True(IsAllCyrillic(TransliterateLatinToCyrillic(Ghbdtn)));
        Assert.True(IsAllLatin(TransliterateCyrillicToLatin(Ruddsch)));
    }

    // ----- NormalizeExceptionToken -----

    [Theory]
    [InlineData(" Example.com. ", "example.com")]
    [InlineData("  NASA!", "nasa")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void NormalizeExceptionToken_TrimsPunctuationAndCasing(string? input, string expected)
    {
        Assert.Equal(expected, NormalizeExceptionToken(input));
    }

    // ----- Bigram-модель -----

    [Fact]
    public void BigramModels_ReturnFiniteScoresForCommonWords()
    {
        Assert.True(double.IsFinite(ScoreBigramWord("hello", WordLanguage.English)));
        Assert.True(double.IsFinite(ScoreBigramWord("\u043f\u0440\u0438\u0432\u0435\u0442", WordLanguage.Russian)));
    }

    // ----- производительность: повторные вызовы не должны падать -----

    [Fact]
    public void CorrectionLookup_HandlesTenThousandInputsWithoutThrowing()
    {
        for (var i = 0; i < 10_000; i++)
        {
            _ = IsCorrectionDesirable("ghbdtn", "\u043f\u0440\u0438\u0432\u0435\u0442", WordLanguage.English);
            _ = IsCorrectionDesirable("hello", "\u0440\u0443\u0434\u0434\u0449", WordLanguage.English);
        }
    }
}