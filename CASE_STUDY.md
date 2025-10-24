# 1) Business context (telecom marketing)

Marketing managers need quick answers to KPI questions (e.g., ARPU trend, churn spikes, net adds) and on-demand forecasts to plan promos and budgets. This case study will focus on the listed KPIs. Visible Alpha frames these as core integrated-telecom metrics and shows how subs volume × ARPU drive revenue. ([S&P Global Market Intelligence][1])

Quick definitions the bot can surface on request:

* **ARPU** = revenue per user over a period. ([Investopedia][2])
* **Churn rate** = subscribers lost ÷ subscribers at start of period. ([Investopedia][3])
* **Net additions** ≈ gross adds − disconnects (net subscribers after churn). ([S&P Global Market Intelligence][4])

# 2) Data model (simple)

Single table `kpi_history`:

| date       | kpi        | segment  |  value | notes               |
| ---------- | ---------- | -------- | -----: | ------------------- |
| 2024-01-31 | arpu       | postpaid |   57.3 | USD                 |
| 2024-01-31 | churn_rate | prepaid  |    2.6 | % of beginning subs |
| 2024-01-31 | net_adds   | postpaid | 18,400 | subs                |

The chatbot will (a) answer lookups/aggregations and (b) call a forecasting action that returns a point forecast + uncertainty.

# 3) System design (Rasa)

* **NLU + Dialogue** in Rasa (intents, entities, slots, responses, rules/forms). In Rasa, the **domain** lists responses, slots, and actions; **forms** gather required slots; **YAML** is used for training data. ([Rasa][5])
* **“Tool calling”** via **Custom Actions**: Rasa invokes Python code to hit databases/APIs and return messages/slot events. (As of Rasa 3.10 it can be even ran Python actions inside the assistant without a separate action server). ([Rasa][6])
* **Forecasting service** inside an action using **Prophet** (fast, additive model with seasonality) or **ARIMA** as a fallback. ([facebook.github.io][7])

## 3.1 Conversation flows (examples)

1. **KPI Q&A**
   **User:** “What was prepaid churn last month?”
   → NLU: `ask_kpi_value` (kpi=churn_rate, segment=prepaid, period=last_month)
   → Form fills missing slots (e.g., period) → `action_query_kpi` returns value and context. ([Rasa][8])

2. **Forecasting**
   **User:** “Forecast next 3 months of postpaid ARPU with Prophet.”
   → NLU: `forecast_kpi` (kpi=arpu, segment=postpaid, horizon=3, model=prophet)
   → Form → `action_forecast_kpi` trains model, returns summary + 80/95% intervals. ([facebook.github.io][7])

# 4) Minimal implementation (starter files)

## `domain.yml`

```yaml
version: "3.1"
intents:
  - greet
  - ask_kpi_value
  - forecast_kpi
entities:
  - kpi
  - segment
  - period
  - horizon
  - model
slots:
  kpi: {type: text, influence_conversation: true}
  segment: {type: text, influence_conversation: true}
  period: {type: text, influence_conversation: true}
  horizon: {type: float, influence_conversation: true}
  model: {type: text, influence_conversation: true, initial_value: "prophet"}
responses:
  utter_greet:
    - text: "Hi! Ask me about ARPU, churn, net adds, etc., or say 'forecast next quarter ARPU'."
  utter_ask_kpi:
    - text: "Which KPI (e.g., ARPU, churn rate, net adds)?"
  utter_ask_segment:
    - text: "Which segment (postpaid, prepaid, business)?"
  utter_ask_period:
    - text: "For what period (e.g., last month, 2024-Q2, last 12 months)?"
  utter_ask_horizon:
    - text: "How many periods ahead should I forecast?"
forms:
  kpi_query_form:
    required_slots:
      - kpi
      - segment
      - period
  forecast_form:
    required_slots:
      - kpi
      - segment
      - horizon
actions:
  - action_query_kpi
  - action_forecast_kpi
session_config:
  session_expiration_time: 60
  carry_over_slots_to_new_session: true
```

(Structures above follow Rasa’s domain/slot/action conventions.) ([Rasa][5])

## `data/nlu.yml`

```yaml
version: "3.1"
nlu:
- intent: greet
  examples: |
    - hi
    - hello
- intent: ask_kpi_value
  examples: |
    - what was [churn rate](kpi) for [prepaid](segment) [last month](period)?
    - show [ARPU](kpi) for [postpaid](segment) in [2024-Q1](period)
    - net adds for [business](segment) [last 12 months](period)
- intent: forecast_kpi
  examples: |
    - forecast [ARPU](kpi) for [postpaid](segment) next [3](horizon) months
    - predict [net additions](kpi) for [prepaid](segment) for the next [2](horizon) quarters using [prophet](model)
    - project [churn rate](kpi) for [postpaid](segment) [6](horizon) months ahead with [arima](model)
```

(Rasa uses YAML to define intents/entities for NLU.) ([Rasa][9])

## `data/rules.yml`

```yaml
version: "3.1"
rules:
- rule: handle KPI lookup
  steps:
    - intent: ask_kpi_value
    - action: kpi_query_form
    - active_loop: kpi_query_form
- rule: submit KPI lookup
  condition:
    - active_loop: kpi_query_form
  steps:
    - action: kpi_query_form
    - active_loop: null
    - action: action_query_kpi

- rule: handle forecasting
  steps:
    - intent: forecast_kpi
    - action: forecast_form
    - active_loop: forecast_form
- rule: submit forecast
  condition:
    - active_loop: forecast_form
  steps:
    - action: forecast_form
    - active_loop: null
    - action: action_forecast_kpi
```

(Forms request missing slots automatically.) ([Rasa][8])

## `actions.py` (the “tool”)

```python
from typing import Any, Dict, List, Text
from rasa_sdk import Action, Tracker
from rasa_sdk.executor import CollectingDispatcher
from rasa_sdk.events import SlotSet
import pandas as pd
from datetime import datetime
import numpy as np

# Optional model backends
USE_PROPHET = True
try:
    from prophet import Prophet
except Exception:
    USE_PROPHET = False
from statsmodels.tsa.arima.model import ARIMA  # fallback

# --- helpers ---
def load_data() -> pd.DataFrame:
    # Expect CSV with columns: date,kpi,segment,value
    df = pd.read_csv("data/kpi_history.csv", parse_dates=["date"])
    df["kpi"] = df["kpi"].str.lower()
    df["segment"] = df["segment"].str.lower()
    return df

def slice_series(df: pd.DataFrame, kpi: str, segment: str) -> pd.DataFrame:
    s = (df[(df.kpi==kpi.lower()) & (df.segment==segment.lower())]
           .sort_values("date")[["date","value"]])
    # Monthly frequency for stability
    s = s.set_index("date").resample("MS").mean().reset_index()
    return s.rename(columns={"date":"ds","value":"y"})

# --- actions ---
class ActionQueryKPI(Action):
    def name(self) -> Text:
        return "action_query_kpi"

    def run(self, dispatcher: CollectingDispatcher, tracker: Tracker, domain: Dict[Text, Any]) -> List[Dict[Text, Any]]:
        kpi = tracker.get_slot("kpi")
        segment = tracker.get_slot("segment")
        period = tracker.get_slot("period")
        df = load_data()

        # naive period handling: last_month, last_12_months, explicit YYYY-Qn, etc.
        ts = slice_series(df, kpi, segment)
        if period and "last" in period:
            n = [int(t) for t in period.split() if t.isdigit()]
            months = n[0] if n else 1
            ts = ts.tail(months)
        msg = f"{kpi.upper()} for {segment.title()} — last {len(ts)} period(s): " + \
              ", ".join([f"{d.strftime('%Y-%m')}: {v:.2f}" for d,v in zip(ts['ds'], ts['y'])])
        dispatcher.utter_message(text=msg)
        return []

class ActionForecastKPI(Action):
    def name(self) -> Text:
        return "action_forecast_kpi"

    def run(self, dispatcher: CollectingDispatcher, tracker: Tracker, domain: Dict[Text, Any]) -> List[Dict[Text, Any]]:
        kpi = tracker.get_slot("kpi")
        segment = tracker.get_slot("segment")
        horizon = int(float(tracker.get_slot("horizon") or 3))
        model = (tracker.get_slot("model") or "prophet").lower()

        df = load_data()
        ts = slice_series(df, kpi, segment)
        if len(ts) < 6:
            dispatcher.utter_message(text="Not enough history to forecast reliably.")
            return []

        if model == "prophet" and USE_PROPHET:
            m = Prophet(interval_width=0.95)
            m.fit(ts)
            future = m.make_future_dataframe(periods=horizon, freq="MS")
            fcst = m.predict(future).tail(horizon)
            out = []
            for _, r in fcst.iterrows():
                out.append(f"{r['ds'].strftime('%Y-%m')}: yhat={r['yhat']:.2f} (80% [{r['yhat_lower']:.2f},{r['yhat_upper']:.2f}])")
            dispatcher.utter_message(text=f"Prophet forecast for {kpi.upper()} ({segment.title()}) next {horizon} month(s):\n" + "\n".join(out))
        else:
            # ARIMA fallback
            series = ts.set_index("ds")["y"].asfreq("MS").fillna(method="ffill")
            order = (1,1,1)  # simple default
            model = ARIMA(series, order=order)
            res = model.fit()
            fc = res.get_forecast(steps=horizon)
            conf = fc.conf_int(alpha=0.20)  # ~80% interval
            pred = fc.predicted_mean
            lines = []
            for i, val in enumerate(pred):
                dt = series.index[-1] + pd.offsets.MonthBegin(i+1)
                lo, hi = conf.iloc[i]
                lines.append(f"{dt.strftime('%Y-%m')}: yhat={val:.2f} (80% [{lo:.2f},{hi:.2f}])")
            dispatcher.utter_message(text=f"ARIMA forecast for {kpi.upper()} ({segment.title()}):\n" + "\n".join(lines))
        return []
```

(Custom actions are how Rasa executes external logic; they can run as a server or, in newer Rasa, directly within the assistant.) ([Rasa][6])
(Prophet/ARIMA choices are standard forecasting approaches for monthly KPIs with seasonality.) ([facebook.github.io][7])

### Sample CSV (put at `data/kpi_history.csv`)

```
date,kpi,segment,value
2023-01-31,arpu,postpaid,55.2
2023-02-28,arpu,postpaid,55.4
...
2023-01-31,churn_rate,prepaid,2.7
...
2023-01-31,net_adds,postpaid,18000
```

# 5) How this meets marketing needs

* Natural-language KPI lookups with definitions for clarity (marketing can ask “What’s ARPU YTD for postpaid?”). ([Investopedia][2])
* One-shot forecasting for planning using Prophet (uncertainty bands; robust to seasonality) or ARIMA as backup. ([facebook.github.io][7])
* Slot-filling ensures required parameters (KPI, segment, horizon) are captured without back-and-forth confusion. ([Rasa][8])

# 6) Success metrics

* **Assistant quality:** intent F1, slot accuracy, action success rate.
* **Business:** forecast MAPE/MdAPE vs. actuals; time saved for routine questions; faster campaign decision cycles.

# 7) Next steps

* Add segment hierarchies (e.g., prepaid vs. postpaid, business/wholesale) and revenue tie-ins (ARPU × average subs) as shown in integrated-telecom models. ([S&P Global Market Intelligence][4])
* Add retrieval-based answers for KPI definitions based on the internal glossary.
* Harden period parsing and model selection (e.g., auto-ARIMA, holiday effects in Prophet). ([facebook.github.io][10])

[1]: https://visiblealpha.com/telecommunications/integrated-telecom-companies/telecom-kpis/ "Integrated Telecom Industry KPIs for Investment Professionals"
[2]: https://www.investopedia.com/terms/a/arpu.asp "Average Revenue Per Unit (ARPU): Definition and How To Calculate"
[3]: https://www.investopedia.com/terms/c/churnrate.asp "Churn Rate: Definitions, Examples, and Calculations"
[4]: https://visiblealpha.com/telecommunications/integrated-telecom-companies/integrated-telecom-kpi-forecasts/ "Interactive Integrated Telecom KPI Forecasts"
[5]: https://rasa.com/docs/reference/config/domain/ "Domain | Rasa Documentation"
[6]: https://rasa.com/docs/pro/build/custom-actions/ "Writing Custom Actions | Rasa Documentation"
[7]: https://facebook.github.io/prophet/ "Prophet | Forecasting at scale. - Meta Open Source"
[8]: https://rasa.com/docs/rasa/forms/ "Forms"
[9]: https://rasa.com/docs/reference/primitives/training-data-format/ "Training Data Format | Rasa Documentation"
[10]: https://facebook.github.io/prophet/docs/quick_start.html "Quick Start | Prophet - Meta Open Source"
