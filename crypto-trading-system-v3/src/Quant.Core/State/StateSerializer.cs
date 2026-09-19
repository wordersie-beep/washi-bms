using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quant.Core.State;

/// <summary>
/// Сериализация состояния через System.Text.Json.
///
/// System.Text.Json выбран потому, что он входит в BCL .NET 6 — то есть это ссылка времени
/// компиляции, а не внешняя библиотека. cTrader Cloud не умеет подгружать сторонние .dll во
/// время исполнения, поэтому любая внешняя зависимость здесь означала бы, что робот
/// работает локально и молча ломается в облаке.
/// </summary>
public static class StateSerializer
{
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public const int CurrentSchemaVersion = 1;

    public static string Serialize(BotState state)
    {
        if (state == null) return null;

        try
        {
            state.SchemaVersion = CurrentSchemaVersion;
            return JsonSerializer.Serialize(state, Options);
        }
        catch (Exception)
        {
            // Провал сериализации не должен останавливать торговлю. Потеря одного снимка
            // состояния — неприятность; исключение в торговом цикле — авария.
            return null;
        }
    }

    /// <summary>
    /// Читает состояние. Возвращает null на любой проблеме — повреждённое, усечённое или
    /// написанное другой версией схемы.
    ///
    /// Отбрасывать целиком, а не разбирать по частям, здесь безопаснее: система без
    /// состояния стартует осторожно и сверится с брокером, тогда как система с
    /// полувосстановленным состоянием уверенно действует на основе того, что может быть
    /// неправдой.
    /// </summary>
    public static BotState Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            BotState state = JsonSerializer.Deserialize<BotState>(json, Options);
            if (state == null) return null;

            if (state.SchemaVersion != CurrentSchemaVersion) return null;

            state.Positions ??= new System.Collections.Generic.List<PersistedPosition>();
            state.ExecutedSignalIds ??= new System.Collections.Generic.List<string>();
            state.Strategies ??= new System.Collections.Generic.List<PersistedStrategy>();
            state.LastSignalBarUtc ??= new System.Collections.Generic.Dictionary<string, DateTime>();

            return state;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
