#!/usr/bin/env python3
"""
Сверяет числовые утверждения документации с кодом.

Цифры в документации уже дважды отставали от кода и описывали систему, которой
нет. Помнить об их обновлении дешевле не получается — значит, пусть проверяет
машина. Каждая проверка здесь соответствует утверждению, которое кто-то прочитает
и на которое будет рассчитывать.

--fix обновляет найденные расхождения вместо того, чтобы падать на них.
"""
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DOCS = sorted(ROOT.glob("*.md")) + sorted((ROOT / "docs").glob("*.md"))


def source_lines() -> int:
    total = 0
    for base in ("src", "tests"):
        for path in (ROOT / base).rglob("*.cs"):
            if "/obj/" in str(path) or "/bin/" in str(path):
                continue
            total += len(path.read_text(encoding="utf-8").splitlines())
    return total


def bot_lines() -> int:
    path = ROOT / "src/QuantCryptoV3/QuantCryptoV3/QuantCryptoV3Bot.cs"
    return len(path.read_text(encoding="utf-8").splitlines())


def strategy_count() -> int:
    text = (ROOT / "src/Quant.Core/Strategies/StrategyRegistry.cs").read_text(encoding="utf-8")
    return len(set(re.findall(r"new\s+(\w+Strategy)\(", text)))


def test_count() -> int:
    result = subprocess.run(
        ["dotnet", "test", str(ROOT / "tests/Quant.Core.Tests"), "--nologo", "-v", "q"],
        capture_output=True, text=True, cwd=ROOT,
    )
    match = re.search(r"Passed:\s+(\d+)", result.stdout)
    if not match:
        print("не удалось определить число тестов:", result.stdout[-400:], file=sys.stderr)
        return -1
    return int(match.group(1))


def grouped(n: int, width: int = 3) -> str:
    """21620 -> '21 620' — так числа записаны в документации."""
    s = str(n)
    parts = []
    while len(s) > width:
        parts.insert(0, s[-width:])
        s = s[:-width]
    parts.insert(0, s)
    return " ".join(parts).replace(" ", " ")


TEST_TABLE_DOC = ROOT / "docs/05-test-plan.md"
TEST_TABLE_START = "<!-- НАЧАЛО: таблица строится scripts/check-docs.py, править руками бессмысленно -->"
TEST_TABLE_END = "<!-- КОНЕЦ -->"


def tests_per_file():
    """Сколько [Fact]/[Theory] в каждом файле тестов."""
    rows = []
    for path in sorted((ROOT / "tests/Quant.Core.Tests").glob("*.cs")):
        text = path.read_text(encoding="utf-8")
        count = len(re.findall(r"^\s*\[(?:Fact|Theory)\]", text, re.M))
        if count:
            rows.append((path.name, count))
    rows.sort(key=lambda r: (-r[1], r[0]))
    return rows


def render_test_table():
    rows = tests_per_file()
    lines = ["| Файл | Методов | О чём |", "|---|---|---|"]

    about = {
        "NumericsTests.cs": "Арифметика, на которой стоит всё остальное",
        "IndicatorTests.cs": "Инкрементальные индикаторы против эталонных расчётов",
        "PipelineTests.cs": "Данные, признаки, классификация режима",
        "StrategyTests.cs": "Девять стратегий и ансамблевое голосование",
        "ProbabilityEvTests.cs": "Байес, калибровка, матожидание, издержки",
        "RiskTests.cs": "Лимиты, просадки, серии, риск разорения, портфель",
        "SizingExitTests.cs": "Размер позиции и планирование выходов",
        "IntegrationTests.cs": "Сквозные прогоны, идемпотентность, восстановление",
        "WarmUpTests.cs": "Прогрев не принимает решений и не портит состояние",
        "M1Tests.cs": "Работа на минутном таймфрейме",
        "DefectTests.cs": "Регрессии на каждый найденный дефект",
        "MarketScheduleTests.cs": "Часы торговли, закрытый рынок, плановые перерывы",
    }

    for name, count in rows:
        lines.append(f"| `{name}` | {count} | {about.get(name, '')} |")

    total = sum(c for _, c in rows)
    lines.append(f"| **Всего** | **{total}** | |")
    lines.append("")
    lines.append(
        f"Методов {total}; прогон выполняет больше — каждый `[Theory]` разворачивается "
        "в отдельный тест на каждый набор входных данных."
    )
    return "\n".join(lines)


def check_test_table(fix: bool) -> int:
    text = TEST_TABLE_DOC.read_text(encoding="utf-8")
    start = text.find(TEST_TABLE_START)
    end = text.find(TEST_TABLE_END)
    if start < 0 or end < 0:
        print("  ПРОПУЩЕНО: маркеры таблицы тестов не найдены")
        return 0

    wanted = TEST_TABLE_START + "\n" + render_test_table() + "\n" + TEST_TABLE_END
    current = text[start:end + len(TEST_TABLE_END)]

    if current == wanted:
        return 0

    if fix:
        TEST_TABLE_DOC.write_text(text[:start] + wanted + text[end + len(TEST_TABLE_END):], encoding="utf-8")
        print("  исправлено docs/05-test-plan.md: таблица состава набора тестов")
        return 0

    print("  РАСХОЖДЕНИЕ docs/05-test-plan.md: таблица состава набора тестов отстала")
    return 1


CHECKS = [
    # (что проверяем, как найти в тексте, как получить истину, как записать истину)
    ("общее число тестов",
     re.compile(r"(?<![0-9])(\d{2,4})(?=\s*(?:тест|теста|тестов)\s*(?:,\s*(?:все|работают)|ловят|проходят))"),
     test_count, str),

    ("число тестов в чек-листе",
     re.compile(r"(?<![0-9])(\d{2,4})/\d{2,4}(?=\s*проходят)"),
     test_count, str),

    ("строк кода",
     re.compile(r"(?<![0-9])(\d{1,3}(?:[  ]\d{3})+)(?=\s*строк C#)"),
     source_lines, grouped),

    ("строк в адаптере бота",
     re.compile(r"~(\d{3,4})(?=\s*строк)"),
     bot_lines, str),

    ("стратегий в ансамбле",
     re.compile(r"(?<![0-9])(\d)(?=\s*стратег)"),
     strategy_count, str),
]


def main() -> int:
    fix = "--fix" in sys.argv
    problems = 0
    truth_cache = {}

    for label, pattern, truth_fn, fmt in CHECKS:
        if truth_fn not in truth_cache:
            truth_cache[truth_fn] = truth_fn()
        truth = truth_cache[truth_fn]
        if truth is None or truth < 0:
            print(f"  ПРОПУЩЕНО: {label} — истину определить не удалось")
            continue

        expected = fmt(truth)

        for doc in DOCS:
            text = doc.read_text(encoding="utf-8")
            updated = text
            for m in list(pattern.finditer(text)):
                found = m.group(1)
                if found.replace(" ", " ") == expected:
                    continue
                rel = doc.relative_to(ROOT)
                if fix:
                    updated = updated[:m.start(1)] + expected + updated[m.end(1):]
                    print(f"  исправлено {rel}: {label} {found} -> {expected}")
                else:
                    line = text[:m.start()].count("\n") + 1
                    print(f"  РАСХОЖДЕНИЕ {rel}:{line}: {label} указано {found}, в коде {expected}")
                    problems += 1
            if fix and updated != text:
                doc.write_text(updated, encoding="utf-8")

    problems += check_test_table(fix)

    if problems:
        print(f"\nрасхождений: {problems}. Запустите: python3 scripts/check-docs.py --fix")
        return 1

    print("  OK: числовые утверждения документации совпадают с кодом")
    return 0


if __name__ == "__main__":
    sys.exit(main())
