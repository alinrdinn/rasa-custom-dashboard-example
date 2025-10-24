from __future__ import annotations

from typing import Any, Dict, Iterable, List, Optional, Text, Tuple

import math
import os

import pandas as pd
from rasa_sdk import Action, Tracker
from rasa_sdk.events import EventType, SlotSet
from rasa_sdk.executor import CollectingDispatcher

# Optional forecasting backends
USE_PROPHET = True
try:
    from prophet import Prophet  # type: ignore
except Exception:  # pragma: no cover - dependency might be missing
    USE_PROPHET = False

USE_ARIMA = True
try:
    from statsmodels.tsa.arima.model import ARIMA  # type: ignore
except Exception:  # pragma: no cover - dependency might be missing
    USE_ARIMA = False


def load_data() -> pd.DataFrame:
    """Load KPI history from CSV, normalising entity casing."""
    csv_path = os.path.join("data", "kpi_history.csv")
    df = pd.read_csv(csv_path, parse_dates=["date"])
    df["kpi"] = df["kpi"].str.lower()
    df["segment"] = df["segment"].str.lower()
    return df


def slice_series(df: pd.DataFrame, kpi: str, segment: str) -> pd.DataFrame:
    """Return a monthly time series dataframe with columns ds/y."""
    series = (
        df[(df["kpi"] == kpi.lower()) & (df["segment"] == segment.lower())]
        .sort_values("date")[["date", "value"]]
        .copy()
    )
    if series.empty:
        return pd.DataFrame(columns=["ds", "y"])
    monthly = series.set_index("date").resample("MS").mean().reset_index()
    monthly.rename(columns={"date": "ds", "value": "y"}, inplace=True)
    return monthly


def parse_last_period(period: str) -> Optional[int]:
    """Return number of months for 'last N' phrases."""
    tokens = period.lower().split()
    if "last" not in tokens:
        return None

    def find_number(words: Iterable[str]) -> Optional[int]:
        for token in words:
            try:
                return int(token)
            except ValueError:
                continue
        return None

    months = find_number(tokens) or 1
    if "quarter" in tokens or "quarters" in tokens:
        months *= 3
    elif "year" in tokens or "years" in tokens:
        months *= 12
    return max(months, 1)


def filter_period(ts: pd.DataFrame, period: Optional[str]) -> Tuple[pd.DataFrame, str]:
    """Filter a time series on requested period and return label."""
    if ts.empty:
        return ts, "no history available"
    if not period:
        return ts.tail(3), f"last {min(len(ts), 3)} period(s)"

    normalized = period.strip().lower()

    if normalized in {"ytd", "year to date"}:
        latest = ts["ds"].max()
        subset = ts[ts["ds"].dt.year == latest.year]
        if not subset.empty:
            return subset, f"{latest.year} YTD"

    if normalized in {"mtd", "month to date"}:
        latest = ts["ds"].max()
        subset = ts[
            (ts["ds"].dt.year == latest.year) & (ts["ds"].dt.month == latest.month)
        ]
        if not subset.empty:
            return subset, latest.strftime("%Y-%m MTD")

    if normalized in {"qtd", "quarter to date"}:
        latest = ts["ds"].max()
        quarter = (latest.month - 1) // 3 + 1
        start_month = (quarter - 1) * 3 + 1
        subset = ts[
            (ts["ds"].dt.year == latest.year)
            & (ts["ds"].dt.month.between(start_month, latest.month))
        ]
        if not subset.empty:
            return subset, f"{latest.year}-Q{quarter} QTD"

    last_months = parse_last_period(normalized)
    if last_months:
        subset = ts.tail(last_months)
        return subset, f"last {len(subset)} period(s)"

    if normalized.startswith("20") and "-q" in normalized:
        year, quarter = normalized.split("-q")
        quarter = quarter.strip()
        year = int(year)
        quarter = int(quarter)
        start_month = (quarter - 1) * 3 + 1
        end_month = start_month + 2
        mask = (ts["ds"].dt.year == year) & (ts["ds"].dt.month.between(start_month, end_month))
        subset = ts.loc[mask]
        if subset.empty:
            return ts.tail(3), f"last {min(len(ts), 3)} period(s)"
        return subset, f"{year}-Q{quarter}"

    try:
        parsed = pd.to_datetime(normalized, errors="raise")
    except Exception:
        return ts.tail(3), f"last {min(len(ts), 3)} period(s)"

    mask = (ts["ds"].dt.year == parsed.year) & (ts["ds"].dt.month == parsed.month)
    subset = ts.loc[mask]
    if subset.empty:
        return ts.tail(3), f"last {min(len(ts), 3)} period(s)"
    label = parsed.strftime("%Y-%m")
    return subset, label


def ensure_positive_int(value: Any, default: int) -> int:
    try:
        number = int(math.ceil(float(value)))
    except Exception:
        return default
    return max(number, 1)


class ActionQueryKPI(Action):
    def name(self) -> Text:
        return "action_query_kpi"

    def run(
        self,
        dispatcher: CollectingDispatcher,
        tracker: Tracker,
        domain: Dict[Text, Any],
    ) -> List[EventType]:
        kpi = (tracker.get_slot("kpi") or "").lower()
        segment = (tracker.get_slot("segment") or "").lower()
        period = tracker.get_slot("period")

        if not kpi or not segment:
            dispatcher.utter_message(text="I need both KPI and segment to look that up.")
            return []

        df = load_data()
        ts = slice_series(df, kpi, segment)
        if ts.empty:
            dispatcher.utter_message(text=f"I could not find any history for {kpi} in the {segment} segment.")
            return []

        subset, label = filter_period(ts, period)
        if subset.empty:
            dispatcher.utter_message(
                text=f"I do not have data for the requested period. Latest value: {ts.tail(1)['y'].iloc[0]:.2f} on {ts.tail(1)['ds'].dt.strftime('%Y-%m').iloc[0]}."
            )
            return []

        msg = ", ".join(f"{row['ds'].strftime('%Y-%m')}: {row['y']:.2f}" for _, row in subset.iterrows())
        dispatcher.utter_message(
            text=f"{kpi.upper()} for {segment.title()} — {label}: {msg}"
        )
        return []


class ActionForecastKPI(Action):
    def name(self) -> Text:
        return "action_forecast_kpi"

    def run(
        self,
        dispatcher: CollectingDispatcher,
        tracker: Tracker,
        domain: Dict[Text, Any],
    ) -> List[EventType]:
        kpi = (tracker.get_slot("kpi") or "").lower()
        segment = (tracker.get_slot("segment") or "").lower()
        horizon_slot = tracker.get_slot("horizon")
        model_slot = (tracker.get_slot("model") or "prophet").lower()

        if not kpi or not segment:
            dispatcher.utter_message(text="I need a KPI and segment before I can run a forecast.")
            return []

        horizon = ensure_positive_int(horizon_slot, default=3)
        df = load_data()
        ts = slice_series(df, kpi, segment)
        if len(ts) < 6:
            dispatcher.utter_message(text="I need at least 6 months of data to forecast reliably.")
            return []

        if model_slot == "prophet" and USE_PROPHET:
            forecast_text = run_prophet_forecast(ts, horizon, kpi, segment)
            dispatcher.utter_message(text=forecast_text)
            return []

        if USE_ARIMA:
            forecast_text = run_arima_forecast(ts, horizon, kpi, segment)
            dispatcher.utter_message(text=forecast_text)
            return [SlotSet("model", "arima" if model_slot != "prophet" else "prophet_unavailable")]

        dispatcher.utter_message(text="Forecasting libraries are not available on the server right now.")
        return []


def run_prophet_forecast(ts: pd.DataFrame, horizon: int, kpi: str, segment: str) -> str:
    model = Prophet(interval_width=0.95)
    model.fit(ts)
    future = model.make_future_dataframe(periods=horizon, freq="MS")
    forecast = model.predict(future).tail(horizon)

    lines = []
    for _, row in forecast.iterrows():
        lines.append(
            f"{row['ds'].strftime('%Y-%m')}: yhat={row['yhat']:.2f} (80% [{row['yhat_lower']:.2f}, {row['yhat_upper']:.2f}])"
        )

    header = f"Prophet forecast for {kpi.upper()} in the {segment.title()} segment ({horizon} month{'s' if horizon > 1 else ''}):"
    return "\n".join([header] + lines)


def run_arima_forecast(ts: pd.DataFrame, horizon: int, kpi: str, segment: str) -> str:
    series = ts.set_index("ds")["y"].asfreq("MS").interpolate(limit_direction="both")
    order = (1, 1, 1)
    model = ARIMA(series, order=order)
    result = model.fit()
    forecast_res = result.get_forecast(steps=horizon)
    conf = forecast_res.conf_int(alpha=0.20)
    pred = forecast_res.predicted_mean

    lines = []
    for i, (dt, value) in enumerate(zip(pred.index, pred.values)):
        lo, hi = conf.iloc[i]
        lines.append(f"{dt.strftime('%Y-%m')}: yhat={value:.2f} (80% [{lo:.2f}, {hi:.2f}])")

    header = f"ARIMA forecast for {kpi.upper()} in the {segment.title()} segment ({horizon} month{'s' if horizon > 1 else ''}):"
    return "\n".join([header] + lines)
