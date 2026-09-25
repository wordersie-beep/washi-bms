"""Phase 6-7: product exclusion engine + opportunity score.

Reads research/data/products_raw.txt (analyst-judgement attributes) and writes
research/data/products_scored.csv plus a markdown summary to stdout.

SCORE != PROFITABILITY. The score only ranks what is worth researching with real
evidence; it is built from judgement inputs, not market data.
"""

from __future__ import annotations

import csv
import sys
from collections import Counter
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RAW = ROOT / "data" / "products_raw.txt"
OUT = ROOT / "data" / "products_scored.csv"

FIELDS = ["P", "W", "F", "E", "R", "S", "I", "Z", "C", "V", "Rp", "B", "D", "MTO"]


@dataclass
class Product:
    niche: str
    name: str
    a: dict[str, int]

    def exclusions(self) -> list[str]:
        """Hard exclusion rules (Phase 6). Every rule states its reason."""
        a = self.a
        reasons = []
        if a["R"]:
            reasons.append("regulated category (cosmetics/food-contact/toy/child/medical/PPE/CLP/supplement/adult)")
        if a["I"]:
            reasons.append("IP risk (patent, dominant trademark or licensed marks)")
        if a["E"]:
            reasons.append("electrical/battery: CE+WEEE+battery compliance and breakage too costly at <=€5k capital")
        if a["W"] == 3:
            reasons.append("heavy/bulky: shipping cost destroys margin")
        if a["F"]:
            reasons.append("fragile: breakage and replacement cost")
        if a["S"]:
            reasons.append("sizing/fit: high return rate under 14-day withdrawal right")
        if a["Z"] == 2:
            reasons.append("extreme single-peak seasonality")
        if a["C"] == 5:
            reasons.append("saturated commodity (Temu/Amazon price anchor)")
        if a["P"] == 1 and not a["Rp"]:
            reasons.append("AOV <€15 with no repeat purchase: cannot carry acquisition cost")
        return reasons

    def score(self) -> float:
        """0-100 filter score. Weights are explicit assumptions."""
        a = self.a
        demand = a["D"] / 5
        competition = (6 - a["C"]) / 5
        margin = min(1.0, (a["P"] / 4) * 0.6 + (a["B"] / 5) * 0.4)
        shipping = (4 - a["W"]) / 3
        returns = 1.0 - 0.5 * a["S"] - 0.3 * a["F"]
        legal = 1.0 - 0.6 * a["R"] - 0.4 * a["I"]
        ads = a["V"] / 5
        brand = a["B"] / 5
        repeat = 1.0 if a["Rp"] else 0.3
        seasonality = 1.0 - a["Z"] * 0.35
        capital = 1.0 if a["MTO"] else 0.6
        weights = {
            "demand": (demand, 14),
            "competition": (competition, 12),
            "margin": (margin, 14),
            "shipping": (shipping, 8),
            "returns": (returns, 8),
            "legal": (legal, 10),
            "ads": (ads, 8),
            "brand": (brand, 8),
            "repeat": (repeat, 4),
            "seasonality": (seasonality, 6),
            "capital": (capital, 8),
        }
        total = sum(w for _, w in weights.values())
        return round(100 * sum(v * w for v, w in weights.values()) / total, 1)


def load() -> list[Product]:
    products = []
    for line in RAW.read_text(encoding="utf-8").splitlines():
        if not line.strip() or line.startswith("#"):
            continue
        parts = line.split("|")
        if len(parts) != 2 + len(FIELDS):
            sys.exit(f"bad row: {line}")
        products.append(Product(parts[0], parts[1], dict(zip(FIELDS, map(int, parts[2:])))))
    return products


def main() -> None:
    products = load()
    niches = {p.niche for p in products}
    rows = []
    for p in products:
        ex = p.exclusions()
        rows.append((p, ex, p.score()))
    with OUT.open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["niche", "product", *FIELDS, "status", "exclusion_reasons", "filter_score"])
        for p, ex, s in sorted(rows, key=lambda r: (bool(r[1]), -r[2])):
            w.writerow([p.niche, p.name, *(p.a[k] for k in FIELDS),
                        "EXCLUDED" if ex else "PASS", "; ".join(ex), s])

    passed = sorted((r for r in rows if not r[1]), key=lambda r: -r[2])
    reason_counts = Counter(reason.split(" (")[0].split(":")[0] for _, ex, _ in rows for reason in ex)
    print(f"candidates: {len(products)}  niches: {len(niches)}")
    print(f"passed exclusion: {len(passed)}  excluded: {len(products) - len(passed)}\n")
    print("| exclusion rule | products hit |\n|---|---|")
    for reason, n in reason_counts.most_common():
        print(f"| {reason} | {n} |")
    print("\n| rank | product | niche | filter score | made-to-order |\n|---|---|---|---|---|")
    for i, (p, _, s) in enumerate(passed, 1):
        print(f"| {i} | {p.name} | {p.niche} | {s} | {'yes' if p.a['MTO'] else 'no'} |")


if __name__ == "__main__":
    main()
