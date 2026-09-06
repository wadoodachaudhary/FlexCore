import asyncio, os, sys
from pathlib import Path
from playwright.async_api import async_playwright, expect
from pypdf import PdfReader
import openpyxl

async def main():
    checks = []
    engine = os.environ.get('FLEXCORE_BROWSER', 'chromium')
    async with async_playwright() as p:
        browser = await getattr(p, engine).launch()
        page = await browser.new_page(viewport={'width': 1300, 'height': 1000}, accept_downloads=True)
        page.set_default_timeout(15000)
        errors = []
        page.on('pageerror', lambda e: errors.append(str(e)))
        page.on('console', lambda m: errors.append(m.text) if m.type == 'error' else None)
        page.on('response', lambda r: errors.append(f'HTTP {r.status}: {r.url}') if r.status >= 400 else None)
        def passed(label): checks.append(label); print('PASS ' + label, flush=True)
        async def button(label): await page.get_by_role('button', name=label, exact=True).click()
        def cell(address): return page.get_by_role('gridcell', name=address, exact=True)
        async def filter_column(column, text, range_text=None):
            await button('Filter column')
            dialog = page.get_by_role('dialog', name='Filter worksheet column')
            if range_text: await dialog.get_by_role('textbox', name='Filter range', exact=True).fill(range_text)
            await dialog.get_by_role('textbox', name='Filter column letter', exact=True).fill(column)
            await dialog.get_by_role('textbox', name='Filter text', exact=True).fill(text)
            await dialog.get_by_role('textbox', name='Filter text', exact=True).press('Enter')
            await expect(dialog).to_have_count(0)
        async def address(value):
            field = page.get_by_role('textbox', name='Cell address', exact=True)
            await field.fill(value); await field.press('Tab')
        try:
            await page.goto(sys.argv[1] + '/flexcore-controls/pdf')
            await expect(page.locator('#bench-status')).to_contain_text('Loaded 3 pages')
            await expect(page.locator('.fx-pdf-text span').first).to_be_visible()
            await page.evaluate("""() => {
                const url = performance.getEntriesByType('resource').map(r=>r.name).find(url=>url.includes('/pdf-viewer.js?'));
                window.pdfModuleVersion = new URL(url).searchParams.get('v');
            }""")
            assert await page.locator('.fx-pdf-page canvas').evaluate('(c)=>c.width>500')
            await expect(page.locator('#pdf-bench-state')).to_contain_text('4 bookmark entries')
            await expect(page.locator('.fx-pdf-bookmarks').get_by_role('button', name='Contents', exact=True)).to_be_disabled()
            await page.locator('.fx-pdf-bookmarks').get_by_role('button', name='Appendix', exact=True).click()
            await expect(page.locator('#pdf-bench-state')).to_contain_text('Page 3 of 3')
            passed('PDF loads real pages and navigates nested internal bookmarks')

            await button('Find alpha via API')
            await expect(page.locator('.fx-pdf-search-status')).to_have_text('1 of 4 matches')
            await expect(page.locator('.fx-pdf-search-layer .fx-pdf-match')).to_have_count(3)
            await expect(page.locator('.fx-pdf-active-match')).to_have_count(1)
            first = await page.locator('.fx-pdf-active-match').bounding_box()
            await button('Next match'); await expect(page.locator('.fx-pdf-search-status')).to_have_text('2 of 4 matches')
            second = await page.locator('.fx-pdf-active-match').bounding_box()
            assert second['x'] > first['x'] + 15
            await button('Next match'); await button('Next match')
            await expect(page.locator('#pdf-bench-state')).to_contain_text('Page 2 of 3')
            await expect(page.locator('.fx-pdf-search-status')).to_have_text('4 of 4 matches')
            await button('Next match'); await expect(page.locator('.fx-pdf-search-status')).to_have_text('1 of 4 matches')
            await button('Previous match'); await expect(page.locator('.fx-pdf-search-status')).to_have_text('4 of 4 matches')
            passed('PDF search visits each occurrence, highlights the active match and wraps across pages')

            query = page.get_by_role('textbox', name='Search document', exact=True)
            await page.get_by_role('checkbox', name='Match case', exact=True).check()
            await query.fill('Alpha'); await query.press('Enter')
            await expect(page.locator('.fx-pdf-search-status')).to_have_text('1 of 1 matches')
            await query.fill('brave new'); await query.press('Enter')
            await expect(page.locator('#bench-status')).to_contain_text("Search 'brave new'")
            await expect(page.locator('.fx-pdf-search-status')).to_have_text('1 of 1 matches')
            assert await page.locator('.fx-pdf-active-match').count() >= 2
            await query.fill('a.*'); await query.press('Enter')
            await expect(page.locator('.fx-pdf-search-status')).to_have_text('0 of 0 matches')
            await query.press('Escape'); await expect(page.locator('.fx-pdf-search-status')).to_have_count(0)
            await expect(page.locator('.fx-pdf-match')).to_have_count(0)
            passed('PDF case matching, literal search, cross-format highlights and Escape cancellation')
            await button('Find alpha via API'); await expect(page.locator('.fx-pdf-search-status')).to_have_text('1 of 4 matches')
            before_zoom = await page.locator('.fx-pdf-active-match').bounding_box()
            await button('+'); await page.wait_for_function('document.querySelector(".fx-pdf-page canvas").style.width === "765px"')
            await expect(page.locator('.fx-pdf-active-match')).to_have_count(1)
            await page.wait_for_function('(width)=>document.querySelector(".fx-pdf-active-match")?.getBoundingClientRect().width > width*1.2', arg=before_zoom['width'])
            after_zoom = await page.locator('.fx-pdf-active-match').bounding_box()
            assert after_zoom['width'] > before_zoom['width'] * 1.2
            await button('Rotate'); await page.wait_for_function('document.querySelector(".fx-pdf-page canvas").style.width === "990px"')
            await page.wait_for_function('document.querySelector(".fx-pdf-active-match")?.getBoundingClientRect().height > 25')
            assert (await page.locator('.fx-pdf-active-match').bounding_box())['height'] > 25
            passed('PDF search geometry follows zoom and rotation')
            await button('Reset sample'); await expect(page.locator('#bench-status')).to_contain_text('Loaded 3 pages')

            name = page.locator('.fx-pdf-sidebar').get_by_role('textbox', name='Name', exact=True)
            await name.fill('Parity customer'); await name.press('Tab')
            await expect(page.locator('.fx-pdf-error')).to_have_count(0)
            await button('Save snapshot'); await expect(page.locator('#bench-status')).to_contain_text('PDF bytes')
            await button('Reload snapshot'); await expect(name).to_have_value('Parity customer')
            await button('Find alpha via API'); await expect(page.locator('.fx-pdf-search-status')).to_have_text('1 of 4 matches')
            async with page.expect_download() as pending: await button('Download')
            output = f'/tmp/flexcore-documents-{engine}.pdf'; await (await pending.value).save_as(output)
            reader = PdfReader(output)
            assert len(reader.pages) == 3 and reader.get_fields()['Name']['/V'] == 'Parity customer'
            assert reader.outline[0]['/Title'] == 'Introduction'
            passed('PDF saved bytes retain form edits, bookmarks and searchable text after reload')
            await page.screenshot(path=f'/tmp/flexcore-pdf-{engine}-bench.png', full_page=True)

            # Exercise real NavigationManager transitions in one live circuit.
            # Full page.goto reloads previously hid disposal failures.
            await page.evaluate("""async () => {
                const module = await import('/_content/FlexCore/pdf-viewer.js?v=' + window.pdfModuleVersion);
                await module.invoke('dispose', null);
                await module.invoke('dispose', null, {}, 'missing-viewer');
                window.pdfNavigationSentinel = 'same-document';
            }""")
            for label, route in [('TreeView', 'tree-view'), ('TreeGrid', 'tree-grid'), ('TreeGrid operations', 'tree-grid-operations')]:
                old_host = await page.locator('.fx-pdf-viewer').element_handle()
                await button('All controls')
                await expect(page).to_have_url(sys.argv[1] + '/flexcore-controls')
                await button(label)
                await expect(page).to_have_url(sys.argv[1] + '/flexcore-controls/' + route)
                await expect(page.get_by_role('button', name='All controls', exact=True)).to_be_visible()
                if route == 'tree-view':
                    await page.locator('[role=treeitem][data-node-id="alpha"]').click()
                    await expect(page.locator('#tree-selection')).to_have_text('Selected: alpha')
                else:
                    await button('Expand all')
                    await expect(page.locator('.fx-treegrid-body [data-node-id="11"]')).to_be_visible()
                assert await page.evaluate('window.pdfNavigationSentinel') == 'same-document'
                # The detached element is intentionally retained by this test, so
                # GC cannot hide a leaked viewer/worker mapping.
                released = await page.evaluate("""async host => {
                    const module = await import('/_content/FlexCore/pdf-viewer.js?v=' + window.pdfModuleVersion);
                    try { await module.invoke('search', host, {query:'alpha'}); return false; }
                    catch (error) { return error.message === 'Open a PDF first.'; }
                }""", old_host)
                assert released
                await old_host.dispose()
                await button('All controls'); await button('PDF Viewer')
                await expect(page.locator('#bench-status')).to_contain_text('Loaded 3 pages')
                await button('Find alpha via API')
                await expect(page.locator('.fx-pdf-search-status')).to_have_text('1 of 4 matches')
                assert not errors, errors
                passed('PDF → All controls → ' + label + ' → PDF keeps the circuit usable and releases detached resources')

            # Disposal must also cancel a pending fetch, without waiting for its response.
            async def delay_pdf(route):
                await asyncio.sleep(1.5)
                try: await route.continue_()
                except Exception: pass  # Navigation aborts the request.
            await page.route('**/samples/pdf-parity.pdf?*', delay_pdf)
            await button('Reset sample')
            await expect(page.locator('#bench-status')).to_have_text('Opening sample')
            await button('All controls'); await button('TreeView')
            await page.locator('[role=treeitem][data-node-id="alpha"]').click()
            await expect(page.locator('#tree-selection')).to_have_text('Selected: alpha')
            await page.wait_for_timeout(1700)
            await page.unroute('**/samples/pdf-parity.pdf?*', delay_pdf)
            assert not errors, errors
            passed('Navigation during PDF loading cancels its request without a circuit error')
            await button('All controls'); await button('Spreadsheet')
            await expect(cell('B2')).to_have_text('2')
            await cell('B2').dblclick(); editor = page.get_by_role('textbox', name='Edit cell', exact=True)
            await editor.fill('5'); await editor.press('Enter'); await expect(cell('D2')).to_have_text('62.5')
            await button('Undo'); await expect(cell('D2')).to_have_text('25')
            passed('Spreadsheet formula editing and undo remain functional')

            await button('Load filter and freeze sample'); await expect(cell('B2')).to_have_text('North')
            await cell('B2').click(); await button('Freeze before selection')
            await expect(page.locator('.fx-sheet-state').first).to_contain_text('Frozen: 1 rows, 1 columns')
            scroll = page.locator('.fx-sheet-scroll')
            await scroll.evaluate('(el)=>{el.style.width="650px";el.scrollTop=0;el.scrollLeft=0;}')
            before = await cell('A1').bounding_box(); moving = await cell('D4').bounding_box()
            await scroll.evaluate('(el)=>{el.scrollTop=160;el.scrollLeft=220;}')
            await page.wait_for_timeout(100)
            after = await cell('A1').bounding_box(); moved = await cell('D4').bounding_box()
            assert abs(after['x'] - before['x']) < 2 and abs(after['y'] - before['y']) < 2
            assert moved['x'] < moving['x'] - 100 and moved['y'] < moving['y'] - 100
            await button('Next rows'); await expect(cell('A26')).to_have_text('Item 025'); await expect(cell('A1')).to_have_text('Item')
            await button('Next columns'); await expect(cell('K26')).to_have_text('K26'); await expect(cell('A1')).to_have_text('Item')
            await button('Previous rows'); await button('Previous columns')
            await scroll.evaluate('(el)=>{el.scrollTop=0;el.scrollLeft=0;}')
            passed('Spreadsheet freezes both axes during real scrolling and keeps panes when paging')

            await filter_column('B', 'North', 'A1:N121')
            await expect(cell('A2')).to_have_text('Item 001'); await expect(cell('A3')).to_have_count(0)
            await expect(cell('A4')).to_have_text('Item 003')
            await cell('B2').click(); await cell('B2').press('ArrowDown'); await expect(cell('B4')).to_be_focused()
            await filter_column('A', '003')
            await expect(cell('A4')).to_have_text('Item 003'); await expect(cell('A2')).to_have_count(0)
            await button('Filter column')
            dialog = page.get_by_role('dialog', name='Filter worksheet column')
            await dialog.get_by_role('textbox', name='Filter column letter').fill('A'); await dialog.get_by_role('button', name='Clear column', exact=True).click()
            await expect(dialog).to_have_count(0); await expect(cell('A2')).to_have_text('Item 001'); await expect(cell('A3')).to_have_count(0)
            passed('Spreadsheet text filters combine across columns, clear independently and keyboard skips hidden rows')

            await cell('B2').dblclick(); await editor.fill('South'); await editor.press('Enter'); await expect(cell('B2')).to_have_count(0)
            await button('Undo'); await expect(page.locator('#bench-status')).to_contain_text('Undo:'); await expect(cell('B2')).to_have_text('North')
            await button('Save snapshot'); await expect(page.locator('#bench-status')).to_contain_text('workbook bytes')
            await button('Clear filters'); await expect(page.locator('#bench-status')).to_contain_text('Clear filters:'); await expect(cell('A3')).to_have_text('Item 002')
            await button('Unfreeze panes'); await button('Reload snapshot'); await expect(cell('A3')).to_have_count(0)
            await expect(page.locator('.fx-sheet-state').first).to_contain_text('Frozen: 1 rows, 1 columns')
            async with page.expect_download() as pending: await button('Save workbook')
            output = f'/tmp/flexcore-documents-{engine}.xlsx'; await (await pending.value).save_as(output)
            book = openpyxl.load_workbook(output); sheet = book.active
            assert sheet.freeze_panes == 'B2' and sheet.auto_filter.ref == 'A1:N121'
            assert sheet.row_dimensions[3].hidden and not sheet.row_dimensions[2].hidden
            assert sheet['E2'].value == '=C2*D2' and sheet.auto_filter.filterColumn
            passed('Spreadsheet filter edits, undo and XLSX reload preserve criteria, hidden rows, formulas and frozen panes')

            await page.get_by_role('checkbox', name='Read only', exact=True).check()
            await expect(page.get_by_role('button', name='Filter column', exact=True)).to_be_disabled()
            await expect(page.get_by_role('button', name='Unfreeze panes', exact=True)).to_be_disabled()
            await button('Clear filters via API')
            await expect(cell('A3')).to_have_count(0)
            await page.screenshot(path=f'/tmp/flexcore-spreadsheet-{engine}-bench.png', full_page=True)
            assert await page.locator('.fx-spreadsheet select,.fx-pdf-viewer select,input[type=date]').count() == 0
            assert not errors, errors
            passed('FlexCore UI controls, read-only guards and no browser or circuit errors')
            print(f'All {len(checks)} document browser checks passed ({engine}).', flush=True)
        except:
            print('ERRORS:', errors, flush=True)
            print((await page.locator('body').inner_text())[-6000:], flush=True)
            await page.screenshot(path=f'/tmp/flexcore-documents-{engine}-failure.png', full_page=True)
            raise
        finally: await browser.close()

asyncio.run(main())
