# E-commerce из Ирландии — исследование, Phases 1–23

> Эта папка — бизнес-исследование и не связана с интеграцией Washi BMS в корне репозитория. Код магазина **не писался** по правилу задания: сначала доказательства, потом разработка.

## Итог в одном абзаце

- **Классический dropshipping из Китая в Ирландию и ЕС в 2026 году — NO-GO.**
  - С 1.07.2026 действует пошлина €3 за item, с ноября ожидается сбор ~€2.
  - Contribution на заказ падает до ~€6, а это ниже любого реалистичного CAC.
- **Лучшая найденная модель — made-to-order POD** (печать в стране покупателя): персонализированная двуязычная карта **townland предков** для ирландской диаспоры в США, продажи на Etsy.
  - Модельный contribution — $21.63 с заказа, минимальный капитал — €250.
- **Вердикт: CONDITIONAL GO только на 6-недельную валидацию.** Разработка магазина — NO-GO до выполнения GO-метрик.

## Карта документов

| Phase | Документ |
|---|---|
| 1 | Ограничения и сценарии капитала → `economics.md` §Phase 18 |
| 2 | `countries.md` |
| 3 | `ireland-business.md` |
| 4 | `logistics.md` |
| 5–7 | `products.md`, `data/products_raw.txt`, `data/products_scored.csv`, `models/score_products.py` |
| 8 | `competitors.md` |
| 9 | `customer-pains.md` |
| 10–11 | `suppliers.md` |
| 12–13 | `platforms.md` |
| 14–18 | `economics.md`, `economics_output.md`, `models/economics.py` |
| 19 | `validation-plan.md` |
| 20–21 | `red-team.md` |
| 22–23, 44, 45 | `decision.md` |
| Источники | `sources.md`: 65 источников с уровнем доверия |

## Как пересчитать

```bash
cd research
python3 models/score_products.py            # отсев и скоринг, пишет data/products_scored.csv
python3 models/economics.py > economics_output.md
```

Замените ESTIMATE и ASSUMPTION во входах `models/economics.py` реальными цифрами (шаг 0 валидации) и перезапустите.

## Ограничение этой сессии

Прямое открытие веб-страниц было заблокировано сетевым прокси. Все факты взяты из поисковой выдачи с URL. Перед тратами их нужно перепроверить на первоисточнике — это шаг 0 в `validation-plan.md`.
