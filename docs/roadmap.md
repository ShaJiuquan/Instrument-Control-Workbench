# Roadmap

## Phase 1: Native Shell

- Avalonia MVVM application shell.
- Instrument list with connection state.
- Run monitor with latest reading, elapsed time, and operator log.
- Mock instruments only.
- Keithley 2450-style source/meter panel.
- Channel-based notification stream into ViewModels.

## Phase 2: Measurement Engine

- Define measurement workflows: timed read, IV sweep, resistance read.
- Add cancellation and safe-stop semantics.
- Record measurement points to an in-memory run buffer.
- Add unit tests for run state transitions.
- Add explicit state-machine transition diagrams to docs.
- Add TTL/lease status visualization.

## Phase 3: Persistence And Export

- Store completed runs in SQLite.
- Export run data to CSV.
- Record instrument configuration and run metadata.

## Phase 4: Realtime Plotting

- Add a plotting package such as ScottPlot.Avalonia or LiveChartsCore.SkiaSharpView.Avalonia.
- Plot live measurement values against elapsed time.
- Add basic axis selection for common fields.

## Phase 5: Hardware Adapters

- Add serial/VISA-style interfaces behind mockable ports.
- Keep real hardware details out of the public default demo.
- Add a sample adapter that speaks a small documented mock protocol.
