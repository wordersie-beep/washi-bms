#!/usr/bin/env python3
"""
Строит справочник параметров бота ИЗ ИСХОДНОГО КОДА.

Существует потому, что таблица параметров, написанная руками, расходится с кодом
на первом же изменении — и расходится молча. Здесь единственный источник правды
один: атрибуты [Parameter] в QuantCryptoV3Bot.cs.

Запуск без аргументов печатает таблицу; с --check сверяет её с BOT-PARAMETERS.md
и возвращает ненулевой код, если они разошлись.
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BOT = ROOT / "src/QuantCryptoV3/QuantCryptoV3/QuantCryptoV3Bot.cs"
OUT = ROOT / "docs/BOT-PARAMETERS.md"

ATTRIBUTE = re.compile(
    r'\[Parameter\(\s*"(?P<label>[^"]+)"(?P<body>.*?)\)\]\s*\n\s*public\s+(?P<type>[\w.<>?]+)\s+(?P<prop>\w+)\s*\{',
    re.S,
)


def field(body: str, name: str):
    m = re.search(name + r'\s*=\s*("(?:[^"\\]|\\.)*"|[^,)\n]+)', body)
    if not m:
        return None
    value = m.group(1).strip()
    if value.startswith('"') and value.endswith('"'):
        value = value[1:-1]
    return value.replace('\\"', '"')


def collect():
    source = BOT.read_text(encoding="utf-8")
    rows = []
    for m in ATTRIBUTE.finditer(source):
        body = m.group("body")
        rows.append({
            "group": field(body, "Group") or "Прочее",
            "label": m.group("label"),
            "default": field(body, "DefaultValue") or "—",
            "min": field(body, "MinValue"),
            "max": field(body, "MaxValue"),
            "description": field(body, "Description") or "",
            "prop": m.group("prop"),
        })
    return rows


def render(rows):
    lines = [
        "# Параметры бота",
        "",
        "Файл собирается автоматически из `QuantCryptoV3Bot.cs` скриптом",
        "`scripts/generate-parameter-reference.py`. Править его руками бессмысленно:",
        "предполётная проверка сверяет его с кодом и падает при расхождении.",
        "",
        "Те же описания видны в cTrader как подсказки к параметрам.",
        "",
    ]

    seen = []
    for row in rows:
        if row["group"] not in seen:
            seen.append(row["group"])

    for group in seen:
        lines.append(f"## {group}")
        lines.append("")
        lines.append("| Параметр | По умолчанию | Диапазон | Что делает |")
        lines.append("|---|---|---|---|")
        for row in rows:
            if row["group"] != group:
                continue
            span = "—"
            if row["min"] is not None and row["max"] is not None:
                span = f"{row['min']} … {row['max']}"
            lines.append(
                f"| {row['label']} | `{row['default']}` | {span} | {row['description']} |"
            )
        lines.append("")

    lines.append(f"Всего параметров: {len(rows)}.")
    lines.append("")
    return "\n".join(lines)


def main():
    rows = collect()
    if not rows:
        print("Ни одного параметра не найдено — изменился формат атрибутов?", file=sys.stderr)
        return 2

    text = render(rows)

    if "--check" in sys.argv:
        if not OUT.exists():
            print(f"{OUT.relative_to(ROOT)} отсутствует", file=sys.stderr)
            return 1
        if OUT.read_text(encoding="utf-8") != text:
            print(f"{OUT.relative_to(ROOT)} разошёлся с кодом — перегенерируйте", file=sys.stderr)
            return 1
        print(f"справочник параметров совпадает с кодом ({len(rows)} параметров)")
        return 0

    OUT.write_text(text, encoding="utf-8")
    print(f"{OUT.relative_to(ROOT)}: {len(rows)} параметров")
    return 0


if __name__ == "__main__":
    sys.exit(main())
