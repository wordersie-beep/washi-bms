using System;
using System.Collections.Generic;
using Quant.Core.Numerics;

namespace Quant.Core.Regime;

/// <summary>
/// Шкала уверенности классификатора режима, построенная по его собственным чтениям.
///
/// Абсолютный порог на этой величине — ошибка проектирования, и она обошлась системе
/// дорого: уверенность классификатора это мера отрыва победителя от второго места, её
/// распределение зависит от числа режимов, инструмента и таймфрейма. Медиана порядка 0.35
/// означает, что порог 0.55 пропускает верхнюю десятую часть чтений — а в сочетании с
/// остальными фильтрами это ноль сделок. На другом инструменте та же цифра означала бы
/// «торговать почти всегда».
///
/// Перцентиль от собственного распределения задаёт одно и то же НАМЕРЕНИЕ независимо от
/// шкалы: торговать там, где режим читается яснее обычного. Тот же приём система уже
/// применяет к ATR и спреду, и ровно по этой причине.
///
/// Абсолютный пол сохраняется: перцентиль без него разрешил бы торговлю в сплошном шуме,
/// если шум одинаков всё время — «лучшее из плохого» всё ещё плохое.
/// </summary>
public sealed class RegimeConfidenceScale
{
    private readonly RollingWindow _window;
    private readonly int _minSamples;

    public RegimeConfidenceScale(int window = 500, int minSamples = 100)
    {
        _window = new RollingWindow(Math.Max(50, window));
        _minSamples = Math.Max(20, minSamples);
    }

    public int Count => _window.Count;

    /// <summary>Набрано ли достаточно чтений, чтобы говорить о распределении.</summary>
    public bool IsCalibrated => _window.Count >= _minSamples;

    public void Observe(double confidence)
    {
        if (MathUtil.IsFinite(confidence)) _window.Add(MathUtil.Clamp01(confidence));
    }

    /// <summary>Медиана — для отчёта: по ней сразу видно, где стоит порог относительно реальности.</summary>
    public double Median => ValueAt(0.50);

    /// <summary>Значение, ниже которого лежит заданная доля наблюдений.</summary>
    private double ValueAt(double percentile)
    {
        int n = _window.Count;
        if (n == 0) return 0;

        var sorted = new List<double>(n);
        for (int i = 0; i < n; i++) sorted.Add(_window[i]);
        sorted.Sort();

        int index = (int)Math.Round(MathUtil.Clamp01(percentile) * (n - 1));
        return sorted[Math.Clamp(index, 0, n - 1)];
    }

    /// <summary>
    /// Порог, выше которого чтение считается ясным.
    ///
    /// До набора выборки действует абсолютный порог: судить о распределении по десятку
    /// наблюдений значит выдавать шум за калибровку.
    /// </summary>
    public double ThresholdFor(double percentile, double absoluteFloor, double absoluteFallback)
    {
        if (!IsCalibrated) return absoluteFallback;

        double fromDistribution = ValueAt(percentile);
        return Math.Max(absoluteFloor, fromDistribution);
    }

    public void Reset() => _window.Clear();
}
