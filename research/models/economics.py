"""Phases 14-18: unit economics, break-even, ad simulation, cash flow, capital.

Every input is tagged FACT (sourced, see research/sources.md), ESTIMATE (method
stated) or ASSUMPTION (judgement to be replaced by validation data). Run:

    python3 models/economics.py > economics_output.md

Nothing here is a forecast of profit. It shows what has to be true for the
model to work, and where it breaks.
"""

from __future__ import annotations

from dataclasses import dataclass, field


@dataclass
class Input:
    value: float
    tag: str  # FACT / ESTIMATE / ASSUMPTION
    note: str


# ---------------------------------------------------------------------------
# F1 — Personalised ancestral townland map print, Etsy, produced in the buyer's
# country by a POD partner (US buyer shown; USD because Etsy US buyers pay USD).
# ---------------------------------------------------------------------------
F1 = {
    "price": Input(49.00, "ASSUMPTION", "18x24in unframed. Benchmarks: Irish Heritage Press printed €39.99 / framed €89.99 (FACT, S-41)"),
    "shipping_charged": Input(0.00, "ASSUMPTION", "free-shipping listing; shipping cost absorbed below"),
    "cogs": Input(13.15, "FACT", "Printful 18x24 poster base price, Aug 2026 (S-38). Verify in dashboard before launch"),
    "ship_cost": Input(6.50, "ESTIMATE", "US poster-tube shipping; UNKNOWN exact, midpoint of $5-8 range typical of POD rate cards - verify"),
    "etsy_listing": Input(0.20, "FACT", "per listing/renewal (S-30)"),
    "etsy_txn_pct": Input(0.065, "FACT", "6.5% of price+shipping (S-30)"),
    "etsy_pp_pct": Input(0.04, "ESTIMATE", "EU seller rate 4% + €0.30 reported for 'many EU countries'; Irish rate not confirmed (S-31)"),
    "etsy_pp_fixed": Input(0.35, "ESTIMATE", "€0.30 converted at ~1.16 USD/EUR (ASSUMPTION on FX)"),
    "fx_pct": Input(0.025, "FACT", "Etsy 2.5% conversion when listing currency != payment account currency (S-30)"),
    "vat_on_fees": Input(0.23, "FACT", "Etsy charges Irish VAT on its fees; not recoverable while not VAT-registered (S-30)"),
    "offsite_ads_share": Input(0.10, "ASSUMPTION", "share of orders attributed to Etsy Offsite Ads (15% fee; mandatory 12% above $10k/yr)"),
    "defect_rate": Input(0.05, "ESTIMATE", "one Gelato reviewer reports 5-7% error rate (S-44); assume half reprinted free by partner"),
    "digital_attach": Input(0.20, "ASSUMPTION", "share of buyers adding a $12 printable PDF of the same map"),
    "digital_price": Input(12.00, "ASSUMPTION", "digital add-on price"),
}

# ---------------------------------------------------------------------------
# F4 — 40x20x25 under-seat cabin bag, Ireland, Shopify + Meta, shipped from China.
# Shown as the control case for classic dropshipping.
# ---------------------------------------------------------------------------
F4 = {
    "price": Input(34.99, "ASSUMPTION", "incl. 23% VAT; Irish retail for this item type not verified"),
    "vat_rate": Input(0.23, "FACT", "Irish standard VAT rate; IOSS import VAT on each consignment <=€150 (S-02, S-05)"),
    "cogs": Input(9.00, "ESTIMATE", "UNKNOWN supplier quote; €7-10 range assumed from marketplace listings, not verified"),
    "ship_cost": Input(6.50, "ESTIMATE", "China->IE tracked small parcel; UNKNOWN - get CJ/YunExpress quotes"),
    "duty": Input(3.00, "FACT", "EU €3 per item from 1 July 2026 (S-01)"),
    "handling_fee": Input(2.00, "ESTIMATE", "EU handling fee expected Nov 2026, amount not yet in legislation (S-06)"),
    "pay_pct": Input(0.014, "FACT", "Shopify Payments IE, EEA cards 1.4% (S-20) - verify in admin"),
    "pay_fixed": Input(0.25, "FACT", "Shopify Payments IE fixed fee (S-20)"),
    "return_rate": Input(0.06, "ASSUMPTION", "14-day withdrawal right; consumer pays return postage if told in advance (S-12)"),
    "return_loss": Input(9.50, "ESTIMATE", "outbound shipping + duty/fees are sunk on a return (€6.50+€3)"),
    "chargeback_rate": Input(0.005, "ASSUMPTION", "0.5% of orders, loss = order value + €15 fee (fee ASSUMPTION)"),
    "support": Input(0.50, "ASSUMPTION", "helpdesk/WISMO cost per order (long China transit drives tickets)"),
}


def f1_unit(price: float | None = None, cogs: float | None = None, ship: float | None = None) -> dict[str, float]:
    p = F1["price"].value if price is None else price
    c = F1["cogs"].value if cogs is None else cogs
    s = F1["ship_cost"].value if ship is None else ship
    gross = p + F1["shipping_charged"].value
    fees = (F1["etsy_listing"].value
            + gross * (F1["etsy_txn_pct"].value + F1["etsy_pp_pct"].value + F1["fx_pct"].value)
            + F1["etsy_pp_fixed"].value)
    fees *= 1 + F1["vat_on_fees"].value
    offsite = F1["offsite_ads_share"].value * 0.15 * gross
    defects = F1["defect_rate"].value * 0.5 * (c + s)
    dig = F1["digital_attach"].value * F1["digital_price"].value
    dig_fees = dig * (F1["etsy_txn_pct"].value + F1["etsy_pp_pct"].value + F1["fx_pct"].value) * (1 + F1["vat_on_fees"].value)
    revenue = gross + dig
    contribution = revenue - c - s - fees - offsite - defects - dig_fees
    return {"revenue": revenue, "cogs": c, "ship": s, "fees": fees + dig_fees, "offsite": offsite,
            "defects": defects, "contribution_before_ads": contribution}


def f1_ownsite() -> float:
    """Own Shopify site selling to US buyers: Shopify Payments non-EEA card 2.4% + fixed (S-20),
    fixed fee taken as $0.30 (ASSUMPTION on FX), no marketplace fees, same COGS/defects."""
    p, c, s = F1["price"].value, F1["cogs"].value, F1["ship_cost"].value
    return p - c - s - (0.024 * p + 0.30) - F1["defect_rate"].value * 0.5 * (c + s)


def f4_unit(price: float | None = None) -> dict[str, float]:
    p = F4["price"].value if price is None else price
    net = p / (1 + F4["vat_rate"].value)
    vat = p - net
    pay = p * F4["pay_pct"].value + F4["pay_fixed"].value
    returns = F4["return_rate"].value * F4["return_loss"].value
    cb = F4["chargeback_rate"].value * (p + 15)
    landed = F4["cogs"].value + F4["ship_cost"].value + F4["duty"].value + F4["handling_fee"].value
    contribution = net - landed - pay - returns - cb - F4["support"].value
    return {"revenue_gross": p, "vat": vat, "revenue_net": net, "landed": landed, "payment": pay,
            "returns": returns, "chargebacks": cb, "support": F4["support"].value,
            "contribution_before_ads": contribution}


def md_inputs(title: str, inputs: dict[str, Input]) -> str:
    rows = [f"### {title} — inputs", "", "| input | value | tag | basis |", "|---|---|---|---|"]
    for k, v in inputs.items():
        rows.append(f"| {k} | {v.value:g} | {v.tag} | {v.note} |")
    return "\n".join(rows)


def breakeven_table() -> str:
    out = ["### Break-even metrics", "",
           "Break-even CPA = contribution before ads. Target CPA = 60% of it (keeps 40% as profit/buffer, ASSUMPTION).",
           "Break-even ROAS = revenue / break-even CPA. Break-even CVR at a given CPC = CPC / break-even CPA.", "",
           "| case | revenue/order | contribution before ads | contribution margin | break-even CPA | target CPA | break-even ROAS | target ROAS |",
           "|---|---|---|---|---|---|---|---|"]
    cases = []
    for price in (39.0, 49.0, 59.0):
        u = f1_unit(price=price)
        cases.append((f"F1 Etsy US, ${price:.0f} print", u["revenue"], u["contribution_before_ads"], "$"))
    u = f1_unit(price=99.0, cogs=38.0, ship=9.0)
    cases.append(("F1 Etsy US, $99 framed (COGS ESTIMATE $38+$9)", u["revenue"], u["contribution_before_ads"], "$"))
    for price in (29.99, 34.99, 39.99):
        u = f4_unit(price=price)
        cases.append((f"F4 Shopify IE, €{price}", u["revenue_net"], u["contribution_before_ads"], "€"))
    for name, rev, contrib, cur in cases:
        be = contrib
        tgt = 0.6 * contrib
        be_roas = rev / be if be > 0 else float("inf")
        t_roas = rev / tgt if tgt > 0 else float("inf")
        out.append(f"| {name} | {cur}{rev:.2f} | {cur}{contrib:.2f} | {contrib / rev:.0%} | {cur}{be:.2f} | {cur}{tgt:.2f} | {be_roas:.2f} | {t_roas:.2f} |")
    out += ["", "Break-even conversion rate by CPC (F1 $49, Etsy Ads):", "",
            "| CPC | break-even CVR | target CVR |", "|---|---|---|"]
    be = f1_unit()["contribution_before_ads"]
    for cpc in (0.20, 0.35, 0.50, 0.75, 1.00):
        out.append(f"| ${cpc:.2f} | {cpc / be:.2%} | {cpc / (0.6 * be):.2%} |")
    out += ["", "Break-even conversion rate by CPC (F4 €34.99, Meta -> Shopify):", "",
            "| CPC | break-even CVR | target CVR |", "|---|---|---|"]
    be4 = f4_unit()["contribution_before_ads"]
    for cpc in (0.40, 0.60, 0.80, 1.00, 1.20):
        out.append(f"| €{cpc:.2f} | {cpc / be4:.2%} | {cpc / (0.6 * be4):.2%} |" if be4 > 0 else f"| €{cpc:.2f} | n/a | n/a |")
    return "\n".join(out)


SCENARIOS = {
    # name: (CPM, CTR, CVR) — ASSUMPTIONS bracketing public 2026 Meta benchmarks
    # (US e-commerce CPM ~$15-25, S-34; global traffic CPC ~$0.78, S-27).
    "VERY BAD": (25.0, 0.006, 0.004),
    "BAD": (20.0, 0.008, 0.007),
    "CONSERVATIVE": (17.0, 0.010, 0.010),
    "BASE": (15.0, 0.012, 0.015),
    "GOOD": (13.0, 0.015, 0.022),
    "EXCELLENT": (11.0, 0.020, 0.030),
}


def ad_sim(label: str, aov: float, contrib: float, cur: str) -> str:
    out = [f"### Ad simulation — {label} (per day, steady state; ASSUMPTIONS in table header)", "",
           "| budget/day | scenario | CPM | impressions | CTR | CPC | sessions | CVR | orders | CAC | revenue | contribution after ads |",
           "|---|---|---|---|---|---|---|---|---|---|---|---|"]
    for budget in (10, 20, 50, 100, 250):
        for name, (cpm, ctr, cvr) in SCENARIOS.items():
            imps = budget / cpm * 1000
            clicks = imps * ctr
            cpc = budget / clicks
            orders = clicks * cvr
            cac = budget / orders if orders else float("inf")
            rev = orders * aov
            profit = orders * contrib - budget
            out.append(f"| {cur}{budget} | {name} | {cur}{cpm:.0f} | {imps:,.0f} | {ctr:.1%} | {cur}{cpc:.2f} | {clicks:.0f} | {cvr:.1%} | {orders:.2f} | {cur}{cac:.2f} | {cur}{rev:.0f} | {cur}{profit:.0f} |")
    return "\n".join(out)


def etsy_ads_sim() -> str:
    """Primary-channel test: Etsy Ads is CPC-billed, so model CPC x CVR directly (S-33, S-37)."""
    contrib = f1_unit()["contribution_before_ads"]
    aov = f1_unit()["revenue"]
    out = ["### Etsy Ads simulation — F1 $49 (primary channel; CPC and CVR ranges from S-33/S-37)", "",
           "| budget/day | scenario | CPC | clicks | CVR | orders | CAC | revenue | contribution after ads |",
           "|---|---|---|---|---|---|---|---|---|"]
    scen = {"VERY BAD": (0.75, 0.005), "BAD": (0.60, 0.010), "CONSERVATIVE": (0.50, 0.015),
            "BASE": (0.35, 0.020), "GOOD": (0.30, 0.030), "EXCELLENT": (0.25, 0.040)}
    for budget in (3, 5, 10, 20):
        for name, (cpc, cvr) in scen.items():
            clicks = budget / cpc
            orders = clicks * cvr
            cac = budget / orders
            out.append(f"| ${budget} | {name} | ${cpc:.2f} | {clicks:.0f} | {cvr:.1%} | {orders:.2f} | ${cac:.2f} | ${orders * aov:.0f} | ${orders * contrib - budget:.2f} |")
    return "\n".join(out)


def cash_flow() -> str:
    out = ["### Working capital needed (peak cash out before first payouts)", "",
           "F1 Etsy: buyer pays at order; POD partner charges the seller's card when the order is submitted (day 0);",
           "Etsy holds new-seller funds ~14 days and can apply rolling reserves up to 45 days (S-36).",
           "F4 Shopify: supplier paid at order (day 0); Meta bills ad spend daily/at threshold; Shopify first payout 7-21 days,",
           "reserves possible (S-35). Worst case models a 30-day hold on new-account funds.", "",
           "| orders/day | F1 hold 14d | F1 hold 45d | F4 hold 7d | F4 hold 30d (incl. ads at target CPA) |", "|---|---|---|---|---|"]
    f1 = f1_unit()
    f1_out = f1["cogs"] + f1["ship"]
    f4 = f4_unit()
    f4_out = f4["landed"]
    f4_cpa = 0.6 * f4["contribution_before_ads"]
    for n in (5, 10, 25, 50, 100):
        out.append(f"| {n} | ${n * f1_out * 14:,.0f} | ${n * f1_out * 45:,.0f} | €{n * (f4_out + f4_cpa) * 7:,.0f} | €{n * (f4_out + f4_cpa) * 30:,.0f} |")
    out += ["", "Refund exposure: F1 personalised goods are exempt from the 14-day withdrawal right (S-12) but defect",
            "reprints still occur; F4 carries withdrawal returns for 14 days after delivery plus chargebacks up to ~120 days",
            "(card-scheme window, ASSUMPTION - verify with processor)."]
    return "\n".join(out)


def capital() -> str:
    lines = ["### Capital scenarios (what is actually needed, not what is available)", "",
             "| item | €250 | €500 | €1,000 | €2,500 | €5,000 | tag |", "|---|---|---|---|---|---|---|"]
    rows = [
        ("Business name registration (CRO)", 20, 20, 20, 20, 20, "FACT S-45"),
        ("Etsy listings (50 x $0.20, ~€0.17)", 9, 9, 17, 17, 17, "FACT S-30"),
        ("Physical samples of own product (3-6 prints to IE)", 60, 90, 120, 150, 150, "ESTIMATE"),
        ("Mapping/design software (QGIS, OSM data)", 0, 0, 0, 0, 0, "FACT (free, open licence)"),
        ("Etsy Ads test", 60, 146, 300, 600, 900, "ASSUMPTION"),
        ("Domain + Shopify (only after validation)", 0, 0, 0, 0, 0, "deferred"),
        ("Meta test for own site (only after validation)", 0, 0, 200, 800, 1500, "ASSUMPTION"),
        ("Accountant VAT/tax opinion (one-off)", 0, 150, 250, 300, 300, "ESTIMATE"),
        ("Working capital for POD orders (float)", 60, 60, 60, 300, 800, "from cash-flow table"),
        ("Refund / reprint reserve", 25, 25, 33, 100, 300, "ASSUMPTION"),
        ("Emergency reserve", 16, 0, 0, 213, 1013, "remainder"),
    ]
    for name, *vals, tag in rows:
        lines.append(f"| {name} | " + " | ".join(f"€{v:,}" for v in vals) + f" | {tag} |")
    totals = [sum(r[i + 1] for r in rows) for i in range(5)]
    lines.append("| **total** | " + " | ".join(f"**€{t:,}**" for t in totals) + " | |")
    return "\n".join(lines)


def unit_table() -> str:
    u1 = f1_unit()
    u4 = f4_unit()
    out = ["### Contribution waterfall", "", "F1 — townland print, Etsy US, $49:", "", "| line | USD |", "|---|---|"]
    for k, v in u1.items():
        out.append(f"| {k} | {v:.2f} |")
    out += ["", "F4 — cabin bag, Shopify IE, €34.99 (China -> IE):", "", "| line | EUR |", "|---|---|"]
    for k, v in u4.items():
        out.append(f"| {k} | {v:.2f} |")
    return "\n".join(out)


def main() -> None:
    f1 = f1_unit()
    f4 = f4_unit()
    parts = [
        "# Economics model output (generated by models/economics.py — do not edit by hand)", "",
        md_inputs("F1 townland print (Etsy, US buyer)", F1), "",
        md_inputs("F4 cabin bag (Shopify, Irish buyer, China-shipped)", F4), "",
        unit_table(), "", breakeven_table(), "",
        etsy_ads_sim(), "",
        ad_sim("F1 via Meta to own Shopify site (phase 2 only)", F1["price"].value, f1_ownsite(), "$"), "",
        ad_sim("F4 via Meta to Shopify", f4["revenue_net"], f4["contribution_before_ads"], "€"), "",
        cash_flow(), "", capital(),
    ]
    print("\n".join(parts))


if __name__ == "__main__":
    main()
