import asyncio, json, os, sys
from playwright.async_api import async_playwright, expect

async def main():
    checks = []
    async with async_playwright() as p:
        engine = os.environ.get('FLEXCORE_BROWSER', 'chromium')
        browser = await getattr(p, engine).launch()
        page = await browser.new_page(viewport={'width': 1500, 'height': 1050})
        page.set_default_timeout(12000)
        errors = []
        failed_responses = []
        page.on('response', lambda r: failed_responses.append(f'{r.status} {r.url}') if r.status >= 400 else None)
        if os.environ.get('FLEXCORE_ASSET_DIAGNOSTICS'):
            page.on('response', lambda r: print('CSS response', r.status, r.url, r.headers, flush=True) if '.css' in r.url else None)
        page.on('pageerror', lambda e: errors.append(str(e)))
        page.on('console', lambda m: errors.append(m.text) if m.type == 'error' else None)
        editor = page.get_by_role('textbox', name='Parity editor', exact=True)
        async def button(name):
            await page.get_by_role('button', name=name, exact=True).click()
            if name == 'Reset sample': await expect(page.locator('#bench-status')).to_have_text('Sample reset')
            if name == 'Load snapshot': await expect(page.locator('#bench-status')).to_have_text('Snapshot restored')
            if name == 'Caret to first paragraph': await expect(editor).to_be_focused()
        def passed(name): checks.append(name); print('PASS ' + name, flush=True)
        async def select_text(block, text=None, end=False):
            await page.evaluate('''({block,text,end}) => {
                const editor=document.getElementById('editor-parity');
                const el=editor.querySelector('[data-block-id="'+block+'"]');
                editor.focus(); const r=document.createRange(); r.selectNodeContents(el);
                if (text) {
                    const w=document.createTreeWalker(el,NodeFilter.SHOW_TEXT);let n;
                    while(n=w.nextNode()) {const i=n.textContent.indexOf(text);if(i>=0){r.setStart(n,i);r.setEnd(n,i+text.length);break;}}
                } else r.collapse(!end);
                const s=getSelection();s.removeAllRanges();s.addRange(r);
            }''', {'block': block, 'text': text, 'end': end})
            await page.wait_for_timeout(80)
            if text: assert await page.evaluate('getSelection().toString()') == text
        async def search(query, expected):
            await page.get_by_role('textbox', name='Find text', exact=True).fill(query)
            await button('Find all')
            await expect(page.get_by_role('search').get_by_role('status')).to_have_text(f'Match 1 of {expected}' if expected else '0 matches')

        try:
            await page.goto(sys.argv[1] + '/flexcore-controls/editor')
            await expect(editor.locator('strong')).to_have_count(2)
            await expect(page.get_by_role('textbox', name='Comparison editor')).to_contain_text('independent')
            passed('EditorControl initializes both instances without host script tags')

            await button('Read blocks')
            blocks = json.loads(await page.locator('#editor-snapshot').inner_text())
            assert len(blocks) == 5 and blocks[3]['Kind'] == 4
            assert '<strong>Alpha</strong>' in blocks[1]['Html'] and '&amp;' in blocks[1]['Html']
            assert '<br>' in blocks[2]['Html']
            await button('Save snapshot'); await button('Load snapshot')
            await expect(editor.locator('strong')).to_have_count(2)
            await expect(editor.locator('.doc-page-break')).to_have_count(1)
            passed('formatted content, escaped text, soft breaks and page breaks round-trip')

            await page.get_by_role('checkbox', name='Echo changes into Blocks').check()
            await select_text('p1', end=True)
            await page.keyboard.type(' typed input', delay=25)
            await expect(editor.locator('[data-block-id="p1"]')).to_contain_text('omega. typed input')
            await expect(page.locator('#bench-status')).to_contain_text('5 blocks received')
            await editor.press('Control+z')
            await expect(editor.locator('[data-block-id="p1"]')).not_to_contain_text('typed input')
            await editor.press('Control+Shift+z')
            await expect(editor.locator('[data-block-id="p1"]')).to_contain_text('typed input')
            await expect(page.get_by_role('textbox', name='Comparison editor')).to_have_text('Comparison content stays independent.')
            passed('typing survives parent binding echoes; keyboard undo/redo remains per-instance')

            await button('Reset sample')
            await select_text('p1', end=True)
            await page.keyboard.press('Enter')
            await page.keyboard.type('A new paragraph')
            await expect(editor).to_contain_text('A new paragraph')
            await button('Read blocks')
            blocks = json.loads(await page.locator('#editor-snapshot').inner_text())
            assert len(blocks) == 6 and len({b['Id'] for b in blocks}) == 6
            assert any('A new paragraph' in b['Text'] for b in blocks)
            await button('Save snapshot'); await button('Load snapshot')
            await expect(editor).to_contain_text('A new paragraph')
            passed('Enter creates a distinct paragraph that survives snapshot reload')

            await button('Reset sample')
            await select_text('p2', 'world')
            await button('Bold')
            await expect(editor.locator('[data-block-id="p2"] strong, [data-block-id="p2"] b')).to_have_count(2)
            await button('Undo')
            await expect(editor.locator('[data-block-id="p2"] strong, [data-block-id="p2"] b')).to_have_count(1)
            await button('Redo')
            await expect(editor.locator('[data-block-id="p2"] strong, [data-block-id="p2"] b')).to_have_count(2)
            passed('toolbar restores editor selection and formatting participates in history')

            await button('Reset sample')
            await editor.press('Control+f')
            await expect(page.get_by_role('textbox', name='Find text')).to_be_focused()
            await search('alpha', 5)
            await page.get_by_role('checkbox', name='Whole word', exact=True).check()
            await search('alpha', 4)
            await page.get_by_role('checkbox', name='Match case', exact=True).check()
            await search('Alpha', 3)
            await button('Next match')
            await expect(page.get_by_role('search').get_by_role('status')).to_have_text('Match 2 of 3')
            await button('Previous match')
            await expect(page.get_by_role('search').get_by_role('status')).to_have_text('Match 1 of 3')
            passed('find shortcut, case/whole-word filters and next/previous navigation')

            await page.get_by_role('checkbox', name='Match case', exact=True).uncheck()
            await search('café', 2)
            passed('whole-word search respects accented letters')
            await page.get_by_role('checkbox', name='Whole word', exact=True).uncheck()
            await search('brave new', 1)
            assert await page.evaluate('getSelection().toString()') == 'brave new'
            passed('search spans inline formatting without changing document markup')

            await page.get_by_role('textbox', name='Replacement text').fill('<literal> & $1')
            await button('Replace current')
            await expect(editor.locator('[data-block-id="p2"]')).to_contain_text('<literal> & $1 world.')
            assert await editor.locator('literal').count() == 0
            await button('Undo')
            await expect(editor.locator('[data-block-id="p2"] strong')).to_have_text('brave')
            passed('replacement is literal and undo restores inline formatting')

            await search('alpha', 5)
            await page.get_by_role('textbox', name='Replacement text').fill('Delta')
            await button('Replace all')
            await expect(editor).not_to_contain_text('Alpha')
            await expect(editor).to_contain_text('Deltabet')
            await button('Undo')
            await expect(editor.locator('[data-block-id="p1"]')).to_contain_text('Alpha alpha alphabet.')
            await expect(editor.locator('[data-block-id="p3"]')).to_contain_text('Alpha on the next page.')
            await button('Redo')
            await expect(editor).to_contain_text('Deltabet')
            passed('replace-all is one undo/redo transaction across blocks')

            await page.get_by_role('checkbox', name='Read only', exact=True).check()
            await expect(editor).to_have_attribute('contenteditable', 'false')
            await expect(page.get_by_role('button', name='Bold', exact=True)).to_be_disabled()
            await expect(page.get_by_role('button', name='Undo', exact=True)).to_be_disabled()
            await search('Delta', 5)
            await expect(page.get_by_role('button', name='Replace all', exact=True)).to_be_disabled()
            before = await editor.inner_text()
            await page.evaluate("fxEditor.replaceSearch('editor-parity',0,'BLOCKED',true);fxEditor.execCommand('editor-parity','bold');fxEditor.undo('editor-parity')")
            assert await editor.inner_text() == before
            passed('read-only blocks mutation APIs while keeping search available')
            await page.get_by_role('checkbox', name='Read only', exact=True).uncheck()
            await button('Close search')

            await button('Reset sample')
            await select_text('p1', end=True)
            await page.keyboard.type(' before layout')
            await expect(editor.locator('[data-block-id="p1"]')).to_contain_text('before layout')
            await page.get_by_role('checkbox', name='Paginated', exact=True).check()
            await expect(editor.locator('.fx-page').first).to_be_visible()
            await expect(editor.locator('[data-block-id="p1"]')).to_contain_text('before layout')
            await select_text('p1', 'alpha')
            await button('Align center')
            await expect(editor.locator('[data-block-id="p1"]')).to_have_attribute('data-align', 'center')
            await expect(editor.locator('[data-block-id="p2"]')).not_to_have_attribute('data-align', 'center')
            await button('Undo')
            await expect(editor.locator('[data-block-id="p1"]')).not_to_have_attribute('data-align', 'center')
            await button('Undo')
            await expect(editor.locator('[data-block-id="p1"]')).not_to_contain_text('before layout')
            passed('layout changes preserve edits/history; paragraph formatting targets blocks inside pages')
            await page.get_by_role('checkbox', name='Paginated', exact=True).uncheck()
            await button('Read blocks')
            blocks = json.loads(await page.locator('#editor-snapshot').inner_text())
            assert len(blocks) == 5 and blocks[3]['Kind'] == 4
            passed('pagination does not accumulate trailing placeholder blocks')

            await button('Copy to comparison editor')
            await expect(page.get_by_role('textbox', name='Comparison editor').locator('strong')).to_have_count(2)
            await button('Caret to first paragraph')
            assert await page.evaluate("document.getElementById('editor-parity').contains(getSelection().anchorNode)")
            assert await page.evaluate("fxEditor.getCaret('editor-parity').blockId") == 'p1'
            passed('caret is scoped correctly when two editors share block IDs')

            await page.keyboard.type('New branch ')
            await expect(page.get_by_role('button', name='Redo', exact=True)).to_be_disabled()
            await expect(editor.locator('[data-block-id="p1"]')).to_contain_text('New branch Alpha')
            passed('a new edit after undo invalidates the old redo branch')

            await button('Reset sample')
            await button('Find / replace')
            await search('alpha', 5)
            await page.evaluate('window.scrollTo(0,0)')
            await page.screenshot(path=f'/tmp/flexcore-editor-{engine}-bench.png', full_page=True)

            assert not errors, errors
            passed('no browser or Blazor circuit errors')
            print(f'All {len(checks)} EditorControl checks passed in {engine}.', flush=True)
        except:
            print('Editor diagnostic:', await page.evaluate("(() => { const e=document.getElementById('editor-parity');return e ? {html:e.innerHTML,selected:getSelection().toString(),saved:e._fxSavedSelection?.toString(),history:window.fxEditor?.historyState(e.id)} : {}; })()"), flush=True)
            await page.screenshot(path=f'/tmp/flexcore-editor-{engine}-failure.png', full_page=True)
            print('Browser errors:', errors, 'HTTP errors:', failed_responses, flush=True)
            raise
        finally:
            await browser.close()

asyncio.run(main())
