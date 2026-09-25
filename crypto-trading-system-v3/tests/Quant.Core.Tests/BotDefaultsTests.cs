using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Quant.Core.Config;
using Quant.Core.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace Quant.Core.Tests;

/// <summary>
/// Значения по умолчанию, объявленные в самом роботе, обязаны давать РАБОЧУЮ конфигурацию.
///
/// Тест написан после отказа, который выглядел так:
///
///     КОНФИГУРАЦИЯ ОТКЛОНЕНА — торговля не начата:
///     * UncalibratedConfidenceThreshold must not be below the absolute floor.
///     cBot stopped itself
///
/// Бот не запускался вообще — ни одной сделки, ни одного бара, мгновенная остановка на
/// свежей установке с нетронутыми настройками. Поле, о котором шла речь, не выведено ни в
/// один параметр, так что исправить это пользователь не мог никак.
///
/// Причина того же класса, что и все остальные в этой системе: два значения по
/// умолчанию в РАЗНЫХ местах. Пол уверенности режима объявлен в атрибуте параметра
/// робота как 0.55, запасной порог — в ядре как 0.45, и проверка их отношения падала.
/// Каждое число по отдельности разумно.
///
/// Ядро проверялось четырьмя сотнями тестов; связка «умолчания робота → конфигурация»
/// не проверялась ничем. Тест читает атрибуты прямо из исходника робота — поэтому
/// разойтись они больше не могут.
/// </summary>
public class BotDefaultsTests
{
    private readonly ITestOutputHelper _out;
    public BotDefaultsTests(ITestOutputHelper output) { _out = output; }

    private const string BotSource = "src/QuantCryptoV3/QuantCryptoV3/QuantCryptoV3Bot.cs";

    /// <summary>[Parameter(... DefaultValue = X ...)] ... public T Name { get; set; }</summary>
    private static readonly Regex ParameterDeclaration = new Regex(
        @"\[Parameter\((?<attr>[^\]]*?)\)\]\s*public\s+(?<type>[\w\.]+)\s+(?<name>\w+)\s*\{\s*get;\s*set;\s*\}",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex DefaultValue =
        new Regex(@"DefaultValue\s*=\s*(?<value>[-\w\.]+)", RegexOptions.Compiled);

    /// <summary>config.Section.Field = ParameterName;</summary>
    private static readonly Regex Assignment = new Regex(
        @"config\.(?<section>\w+)\.(?<field>\w+)\s*=\s*(?<name>\w+);", RegexOptions.Compiled);

    [Fact]
    public void TheBotsOwnDefaultsProduceAConfigurationThatStarts()
    {
        string source = File.ReadAllText(SourcePath(BotSource));

        Dictionary<string, string> defaults = ParameterDefaults(source);
        Assert.True(defaults.Count >= 20,
            $"из исходника робота прочитано лишь {defaults.Count} параметров — тест читает не то");

        var config = new EngineConfig();
        int applied = 0;

        foreach (Match m in Assignment.Matches(BuildConfigBody(source)))
        {
            string parameterName = m.Groups["name"].Value;
            if (!defaults.TryGetValue(parameterName, out string literal)) continue;

            object section = typeof(EngineConfig).GetProperty(m.Groups["section"].Value)?.GetValue(config);
            PropertyInfo field = section?.GetType().GetProperty(m.Groups["field"].Value);
            if (field == null) continue;

            object value = Parse(literal, field.PropertyType);
            if (value == null) continue;

            field.SetValue(section, value);
            applied++;
        }

        _out.WriteLine($"применено умолчаний робота: {applied}");
        Assert.True(applied >= 15, $"применено лишь {applied} умолчаний — связка разобрана неверно");

        // Робот по умолчанию накладывает пресет CryptoM5 поверх полей.
        Presets.CryptoSmallTimeframe(config, Tf.M5);
        config.Mode = OperatingMode.Paper;

        IReadOnlyList<string> problems = config.Validate();
        foreach (string p in problems) _out.WriteLine("  " + p);

        Assert.True(problems.Count == 0,
            "на нетронутых настройках робот откажется стартовать: " + string.Join(" | ", problems));
    }

    [Fact]
    public void TheSameHoldsWithoutThePresetForAnyoneWhoChoosesManual()
    {
        // Пресет чинить чужие умолчания не обязан: кто выбрал Manual, получает ровно поля
        // робота, и они тоже должны складываться в запускаемую конфигурацию.
        string source = File.ReadAllText(SourcePath(BotSource));
        Dictionary<string, string> defaults = ParameterDefaults(source);

        var config = new EngineConfig { Mode = OperatingMode.Paper };

        foreach (Match m in Assignment.Matches(BuildConfigBody(source)))
        {
            if (!defaults.TryGetValue(m.Groups["name"].Value, out string literal)) continue;

            object section = typeof(EngineConfig).GetProperty(m.Groups["section"].Value)?.GetValue(config);
            PropertyInfo field = section?.GetType().GetProperty(m.Groups["field"].Value);
            object value = field == null ? null : Parse(literal, field.PropertyType);
            if (value != null) field.SetValue(section, value);
        }

        IReadOnlyList<string> problems = config.Validate();
        foreach (string p in problems) _out.WriteLine("  " + p);

        Assert.True(problems.Count == 0,
            "режим Manual на умолчаниях робота не стартует: " + string.Join(" | ", problems));
    }

    [Fact]
    public void TheGuardWouldHaveCaughtTheDefectThatMotivatedIt()
    {
        // Сторож, не умеющий поймать ту самую ошибку, — это не сторож.
        var config = new EngineConfig();
        config.Regime.MinConfidenceToTrade = 0.55;
        config.Regime.UncalibratedConfidenceThreshold = 0.45;

        // Больше не отказ: отношение соблюдается по построению.
        Assert.Empty(config.Validate());
        Assert.Equal(0.55, config.Regime.EffectiveUncalibratedThreshold, 6);

        // И подъём идёт только в сторону разборчивости, никогда наоборот.
        config.Regime.UncalibratedConfidenceThreshold = 0.80;
        Assert.Equal(0.80, config.Regime.EffectiveUncalibratedThreshold, 6);
    }

    private static string BuildConfigBody(string source)
    {
        int start = source.IndexOf("private EngineConfig BuildConfig()", StringComparison.Ordinal);
        Assert.True(start > 0, "в исходнике робота не найден BuildConfig");
        return source.Substring(start);
    }

    private static Dictionary<string, string> ParameterDefaults(string source)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in ParameterDeclaration.Matches(source))
        {
            Match d = DefaultValue.Match(m.Groups["attr"].Value);
            if (d.Success) map[m.Groups["name"].Value] = d.Groups["value"].Value;
        }
        return map;
    }

    private static object Parse(string literal, Type target)
    {
        if (target == typeof(double))
            return double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? (object)d : null;
        if (target == typeof(int))
            return int.TryParse(literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? (object)i : null;
        if (target == typeof(bool))
            return bool.TryParse(literal, out bool b) ? (object)b : null;
        if (target == typeof(string))
            return literal;
        if (target.IsEnum)
        {
            string name = literal.Contains('.') ? literal.Substring(literal.LastIndexOf('.') + 1) : literal;
            return Enum.IsDefined(target, name) ? Enum.Parse(target, name) : null;
        }
        return null;
    }

    private static string SourcePath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, relative);
    }
}
