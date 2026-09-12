# Python/Tkinter experiment reference

This directory preserves the pre-production Cutwork experiments that informed the current C# / .NET / WPF application.

It is an **executable behavioral/reference implementation**, not the production runtime.

Production code lives under the repository `src/` directory and must not import or execute this experiment.

## Setup on Windows

From the repository root:

```powershell
cd experiments\python-tkinter
py -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
```

## Canonical late-stage prototype

```powershell
python app_clone_guriguri.py
```

Other preserved comparison launchers:

- `app.py`
- `app_polygon_guriguri.py`
- `app_boundary_guriguri.py`
- `app_guriguri.py`

These exist for comparison and migration evidence. They should not drive the production application structure.

## Tests

Run the complete experiment regression suite from this directory:

```powershell
python -m unittest discover -s tests -t . -p "test_*.py" -v
```

Useful focused checks:

```powershell
python -m py_compile app_clone_guriguri.py clone_brush.py
python -m unittest -v tests.test_clone_brush
```

## Phase 0 benchmark

Headless/repeatable benchmark:

```powershell
python benchmark_python_prototype.py
```

Manual Windows/Tk instrumentation:

```powershell
$env:CUTWORK_BENCHMARK = "1"
$env:CUTWORK_BENCHMARK_OUTPUT = "clone-benchmark.jsonl"
python app_clone_guriguri.py
```

See [`../../docs/python-prototype-benchmark.md`](../../docs/python-prototype-benchmark.md) for the recorded baseline and interpretation.

## Preserved findings

The experiment is retained mainly for:

- Guriguri algorithm behavior;
- Clone Repair source/destination semantics;
- immutable-original sampling;
- Hole Only behavior;
- Patch/Repair composition experiments;
- deterministic image-operation regression tests; and
- Phase 0 performance comparison.

Do not extend this directory into a second production application. New product features belong in the C# / WPF implementation unless a deliberately scoped experiment is approved.
