"""
UI-Tests (Playwright) für SalesofLoga.exe – laufen in GitHub Actions auf Windows gegen die veröffentlichte EXE.

  python tests/e2e/test_desktop.py --exe publish/SalesofLoga.exe --out artifacts

Erzeugt: <out>/ui-tests.html (Bericht), <out>/ui-tests.json, <out>/screenshots/*.png
Exit-Code 0 = alle Tests bestanden.
"""
import argparse, html, json, os, shutil, subprocess, sys, tempfile, time, traceback
from datetime import datetime
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "tests" / "fixtures" / "webdata"

ap = argparse.ArgumentParser()
ap.add_argument("--exe", required=True)
ap.add_argument("--out", default="artifacts")
ap.add_argument("--headed", action="store_true")
args = ap.parse_args()
out = Path(args.out); shots = out / "screenshots"; shots.mkdir(parents=True, exist_ok=True)
data = Path(tempfile.mkdtemp(prefix="slg-ui-"))
pdf = data / "sample-quote.pdf"; pdf.write_bytes(b"%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n")
fake = data / "invoice.pdf"; fake.write_bytes(b"MZ\x90\x00\x03\x00\x00\x00 not a pdf")

results = []
def check(tid, title, cond, info=""):
    results.append({"id": tid, "title": title, "ok": bool(cond), "info": str(info)})
    print(f"{'PASS' if cond else 'FAIL'}  {tid} {title}" + ("" if cond else f"  [{info}]"), flush=True)

proc = subprocess.Popen([args.exe, "--no-browser", "--data", str(data / "app"), "--import", str(FIXTURES)])
console_errors = []
try:
    inst = data / "app" / "instance.json"
    for _ in range(240):
        if inst.exists(): break
        time.sleep(0.25)
    info = json.loads(inst.read_text())
    base = f"http://127.0.0.1:{info['port']}"

    with sync_playwright() as p:
        browser = p.chromium.launch(headless=not args.headed)
        ctx = browser.new_context(viewport={"width": 1440, "height": 900}, accept_downloads=True)
        page = ctx.new_page()
        page.on("console", lambda m: console_errors.append(m.text) if m.type == "error" else None)
        page.on("pageerror", lambda e: console_errors.append(str(e)))
        page.on("requestfailed", lambda r: console_errors.append(f"request failed: {r.url} {r.failure}"))
        page.on("response", lambda r: console_errors.append(f"HTTP {r.status} {r.url}") if r.status >= 400 else None)

        def ready():
            # Kein wait_for_function: das nutzt eval und wird (korrekt) von der CSP der App blockiert
            for _ in range(80):
                if page.evaluate("() => typeof window.Blazor !== 'undefined'"):
                    break
                page.wait_for_timeout(250)
            else:
                page.screenshot(path=str(shots / "fail-blazor-start.png"), full_page=True)
                (out / "fail-page.html").write_text(page.content(), encoding="utf-8")
                raise TimeoutError("Blazor did not start; console: " + " | ".join(console_errors[-8:]))
            page.wait_for_timeout(800)  # Circuit aufbauen

        def go(path):
            page.goto(base + path); ready()

        def step(tid, title, fn):
            try:
                r = fn()
                ok, inf = (r if isinstance(r, tuple) else (True, ""))
                check(tid, title, ok, inf)
            except Exception as e:
                check(tid, title, False, f"{type(e).__name__}: {e}")
                page.screenshot(path=str(shots / f"fail-{tid}.png"), full_page=True)

        # --- Login über Einmal-Link
        page.goto(f"{base}/auth?k={info['token']}"); ready()
        step("UI-01", "Login link opens dashboard with KPIs", lambda: (page.locator(".kpi").count() == 6, page.locator(".kpi").count()))
        page.screenshot(path=str(shots / "01-dashboard.png"), full_page=True)

        # --- Quotation: Validierung
        go("/quotation")
        def empty_submit():
            page.click("#submit-quotation")
            page.wait_for_selector("#quotation-qNo-err")
            return (page.inner_text("#quotation-qNo-err") == "Required", page.inner_text("#quotation-qNo-err"))
        step("UI-02", "Empty submit shows required-field errors", empty_submit)

        def fill_quotation(no, value, date="2026-11-24", customer="Websol", status="Open"):
            page.fill("#quotation-qDate", date)
            page.fill("#quotation-qNo", no); page.keyboard.press("Tab")
            page.select_option("#quotation-customer", customer)
            page.select_option("#quotation-status", status)
            page.fill("#quotation-value", value); page.keyboard.press("Tab")

        def create_with_pdf():
            fill_quotation("QT-2026-0900", "77500.25")
            page.set_input_files("#quotation-attachment", str(pdf))
            page.wait_for_selector("text=Selected: sample-quote.pdf")
            page.click("#submit-quotation")
            row = page.locator("#table-quotation tbody tr", has_text="QT-2026-0900")
            expect(row).to_have_count(1, timeout=10000)
            t = row.inner_text()
            return ("₹77,500.25" in t and row.locator("a", has_text="PDF").count() == 1, t)
        step("UI-03", "Quotation with PDF is saved and listed", create_with_pdf)
        page.screenshot(path=str(shots / "02-quotation.png"), full_page=True)

        def disguised_upload():
            page.set_input_files("#quotation-attachment", str(fake))
            page.wait_for_selector("#quotation-attachment-err")
            return ("signature" in page.inner_text("#quotation-attachment-err"), page.inner_text("#quotation-attachment-err"))
        step("UI-04", "Disguised executable is rejected at upload", disguised_upload)

        def duplicate():
            page.click("#form-quotation >> text=Clear")
            fill_quotation("qt-2026-0900", "1")
            page.click("#submit-quotation")
            page.wait_for_selector("#quotation-qNo-err")
            return ("already exists" in page.inner_text("#quotation-qNo-err"), page.inner_text("#quotation-qNo-err"))
        step("UI-05", "Duplicate quotation number (case-insensitive) is blocked", duplicate)

        def decimals():
            page.fill("#quotation-qNo", "QT-2026-0901"); page.fill("#quotation-value", "1.234"); page.keyboard.press("Tab")
            page.click("#submit-quotation")
            page.wait_for_selector("#quotation-value-err")
            return ("decimals" in page.inner_text("#quotation-value-err"), page.inner_text("#quotation-value-err"))
        step("UI-06", "More than two decimals are rejected", decimals)

        def double_click():
            page.click("#form-quotation >> text=Clear")
            fill_quotation("QT-2026-0902", "500")
            page.dblclick("#submit-quotation")
            expect(page.locator("#table-quotation tbody tr", has_text="QT-2026-0902")).to_have_count(1, timeout=10000)
            page.wait_for_timeout(800)
            n = page.locator("#table-quotation tbody tr", has_text="QT-2026-0902").count()
            return (n == 1, f"{n} rows")
        step("UI-07", "Double click creates exactly one record", double_click)

        def edit():
            page.locator("#table-quotation tbody tr", has_text="QT-2026-0900").locator("text=Edit").click()
            page.wait_for_selector("#cancel-edit-quotation")
            page.select_option("#quotation-status", "Closed")
            page.click("#submit-quotation")
            row = page.locator("#table-quotation tbody tr", has_text="QT-2026-0900")
            expect(row.locator(".pill")).to_have_text("Closed", timeout=10000)
            return (row.locator("a", has_text="PDF").count() == 1, "attachment kept")
        step("UI-08", "Edit changes status and keeps the attachment", edit)

        def delete():
            page.locator("#table-quotation tbody tr", has_text="QT-2026-0902").locator("text=Delete").click()
            page.click(".modal >> button:has-text('Delete')")
            expect(page.locator("#table-quotation tbody tr", has_text="QT-2026-0902")).to_have_count(0, timeout=10000)
            return (True, "")
        step("UI-09", "Delete (soft) removes the record from the list", delete)

        def search():
            page.select_option("#quotation-s-customer", "TP Solar")
            page.click("#search-quotation button[type=submit]")
            page.wait_for_timeout(600)
            n = page.locator("#table-quotation tbody tr").count()
            page.click("#search-quotation >> text=Reset"); page.wait_for_timeout(400)
            return (n == 1, f"{n} rows")
        step("UI-10", "Search filters by customer", search)

        # --- Stock: XSS über die UI
        go("/stock")
        def xss():
            payload = '<img src=x onerror="window.__xss=1">'
            page.fill("#stock-stockDate", "2026-11-25"); page.fill("#stock-partId", "P-900"); page.keyboard.press("Tab")
            page.fill("#stock-description", payload); page.keyboard.press("Tab")
            page.fill("#stock-qty", "2"); page.keyboard.press("Tab")
            page.click("#submit-stock")
            expect(page.locator("#table-stock tbody tr", has_text="P-900")).to_have_count(1, timeout=10000)
            page.wait_for_timeout(500)
            executed = page.evaluate("() => window.__xss === 1")
            shown = payload in page.inner_text("#table-stock")
            return (not executed and shown, f"executed={executed}, shown as text={shown}")
        step("UI-11", "XSS payload entered in the UI is shown as text, not executed", xss)
        page.screenshot(path=str(shots / "03-stock.png"), full_page=True)

        # --- PO Customer: Kunde aus Angebot übernehmen
        go("/po-customer")
        def autofill():
            page.fill("#poCustomer-qNo", "QT-2026-0812"); page.keyboard.press("Tab")
            page.wait_for_timeout(500)
            return (page.input_value("#poCustomer-customer") == "EMMVEE", page.input_value("#poCustomer-customer"))
        step("UI-12", "Customer PO takes the customer from the linked quotation", autofill)

        # --- CSV-Export
        go("/stock")
        def csv():
            with page.expect_download() as dl:
                page.click("#search-stock >> text=Export CSV")
            text = Path(dl.value.path()).read_text(encoding="utf-8-sig")
            return ("'=HYPERLINK" in text and "P-900" in text, text.splitlines()[0][:80])
        step("UI-13", "CSV export works and neutralises formulas", csv)

        # --- Security Check Seite (Admin-Button)
        go("/security")
        def security():
            page.click("#run-security")
            page.wait_for_selector("#sec-summary", timeout=60000)
            passed, failed = page.inner_text("#sec-passed"), page.inner_text("#sec-failed")
            return (failed == "0" and int(passed) >= 24, f"passed {passed}, failed {failed}")
        step("UI-14", "Security Check page: admin button runs all checks", security)
        page.screenshot(path=str(shots / "04-security-check.png"), full_page=True)
        def report():
            with ctx.expect_page() as np_:
                page.click("text=Open report")
            rp = np_.value; rp.wait_for_load_state()
            ok = "Security Check" in rp.title() and rp.locator("table tbody tr").count() >= 24
            rp.screenshot(path=str(shots / "05-security-report.png"), full_page=True); rp.close()
            return (ok, rp.url if not ok else "")
        step("UI-15", "Security report opens as a standalone HTML page", report)

        # --- Settings: Stammdaten + Admin
        go("/settings")
        def settings():
            account = page.locator("dt:has-text('Windows account') + dd code").inner_text()
            page.fill("#md-admins", account)
            page.fill("#md-customers", "TP Solar\nEMMVEE\nPremier\nRenew\nWebsol\nTata Bangalore\nJupiter\nAdani Solar")
            page.click("#master-form button[type=submit]")
            page.wait_for_selector("text=Master data saved.")
            go("/quotation")
            opts = page.locator("#quotation-customer option").all_inner_texts()
            return ("Adani Solar" in opts, f"{account}; {len(opts)} options")
        step("UI-16", "Master data and administrators can be maintained", settings)
        go("/settings")
        page.screenshot(path=str(shots / "06-settings.png"), full_page=True)

        csp = [e for e in console_errors if "Content Security Policy" in e or "Refused to" in e]
        check("UI-17", "No Content-Security-Policy violations in the browser", not csp, " | ".join(csp[:3]))
        other = [e for e in console_errors if e not in csp and "favicon" not in e and "status of 4" not in e]
        check("UI-18", "No JavaScript errors in the browser console", not other, " | ".join(other[:3]))
        browser.close()
except Exception:
    traceback.print_exc()
    print("Browser console / network errors:\n  " + "\n  ".join(console_errors[-30:]))
    check("UI-00", "Test run completed", False, traceback.format_exc(limit=2))
finally:
    proc.terminate()
    try: proc.wait(10)
    except Exception: proc.kill()
    logs = data / "app" / "logs"
    if logs.exists():
        for f in logs.glob("*.log"):
            shutil.copy(f, out / f"app-{f.name}")
            if any(not r["ok"] for r in results):
                print(f"--- {f.name} (last 40 lines) ---"); print("\n".join(f.read_text(encoding="utf-8").splitlines()[-40:]))

passed = sum(r["ok"] for r in results); failed = len(results) - passed
(out / "ui-tests.json").write_text(json.dumps(results, indent=2, ensure_ascii=False), encoding="utf-8")
rows = "\n".join(f"<tr><td><code>{r['id']}</code></td><td>{html.escape(r['title'])}</td><td><code>{html.escape(r['info'])}</code></td>"
                 f"<td><span class='s {'PASS' if r['ok'] else 'FAIL'}'>{'PASS' if r['ok'] else 'FAIL'}</span></td></tr>" for r in results)
(out / "ui-tests.html").write_text(f"""<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>UI tests – SalesofLoga</title>
<style>body{{font-family:"Segoe UI",Arial,sans-serif;margin:0;background:#f4f5f7;color:#1d1f23}}header{{background:#c90c0e;color:#fff;padding:20px 32px}}
header h1{{margin:0;font-size:20px}}main{{padding:24px 32px;max-width:1100px}}table{{width:100%;border-collapse:collapse;background:#fff;font-size:13.5px}}
th,td{{padding:9px 10px;border-bottom:1px solid #e2e4e8;text-align:left}}th{{background:#fafafb;font-size:12px;text-transform:uppercase;color:#555b66}}
.s{{font-weight:700;border-radius:999px;padding:2px 10px;font-size:12px}}.PASS{{background:#e6f4ea;color:#1e7b3a}}.FAIL{{background:#fbe9e9;color:#a10a0c}}
.sum{{font-size:18px;font-weight:700;margin-bottom:16px}}</style></head><body><header><h1>UI tests (Playwright) – SalesofLoga.exe</h1></header><main>
<div class="sum">{passed} passed · {failed} failed · {datetime.now():%Y-%m-%d %H:%M}</div>
<table><thead><tr><th>ID</th><th>Test</th><th>Details</th><th>Result</th></tr></thead><tbody>{rows}</tbody></table></main></body></html>""", encoding="utf-8")
print(f"\n{passed}/{len(results)} passed")
shutil.rmtree(data, ignore_errors=True)
sys.exit(0 if failed == 0 else 1)
