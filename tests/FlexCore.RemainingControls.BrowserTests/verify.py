import asyncio,sys,os,re
from pathlib import Path
import xml.etree.ElementTree as ET
import openpyxl
from playwright.async_api import async_playwright,expect
checks=0
async def check(ok,label):
 global checks
 assert ok,label
 checks+=1;print('PASS '+label,flush=True)
async def main():
 async with async_playwright() as p:
  engine=os.environ.get('FLEXCORE_BROWSER','chromium')
  browser=await getattr(p,engine).launch()
  context=await browser.new_context(viewport={'width':1500,'height':1050},permissions=['clipboard-read','clipboard-write'] if engine=='chromium' else [])
  page=await context.new_page();page.set_default_timeout(12000);errors=[]
  page.on('pageerror',lambda e:errors.append(str(e)))
  page.on('console',lambda m:errors.append(m.text) if m.type=='error' and ('Error:' in m.text or 'circuit' in m.text.lower()) else None)
  async def button(name):await page.get_by_role('button',name=name,exact=True).click()
  async def status(text):await expect(page.locator('#bench-status')).to_have_text(text)
  try:
   await page.goto(sys.argv[1]+'/flexcore-controls');await page.get_by_role('button',name='Spreadsheet',exact=True).wait_for();await check(await page.locator('button').count()==37,'37 independent benches linked')
   slugs=['popup','tooltip','popover','button-group','segmented','chip','chip-list','avatar','floating-label','stack-layout','animation-container','media-query','loader','loader-container','chunk-progress-bar','masked-text-box','range-slider','date-range-picker','drop-down-tree','color-gradient','flat-color-picker','signature','upload','drop-zone','prompt-box','chat','inline-ai-prompt','smart-paste-button','speech-to-text-button','spreadsheet','scheduler','map']
   only=os.environ.get('FLEXCORE_REMAINING_ONLY')
   for slug in slugs:
    if only and slug not in only.split(','):continue
    await page.goto(sys.argv[1]+'/flexcore-controls/'+slug);await page.locator('#bench-status').wait_for();await page.wait_for_timeout(250)
    await check(await page.locator('h1').count()==1,slug+' bench renders')
    if slug=='popup':
     await button('Open popup');field=page.get_by_role('textbox',name='Popup value');await field.fill('Popup committed');await button('Apply popup value');await status('Popup committed');await field.press('Escape');await expect(page.get_by_role('dialog',name='Popup bench')).to_have_count(0);await check(True,'popup commit and Escape')
    elif slug=='tooltip':
     await page.get_by_role('button',name='Focus for help').focus();await expect(page.get_by_role('tooltip')).to_contain_text('Keyboard help');await check(True,'tooltip keyboard focus')
    elif slug=='popover':
     await button('Show details');await page.get_by_role('textbox',name='Record name').fill('Reviewed');await button('Apply details');await status('Reviewed');await check(True,'popover commit')
    elif slug=='button-group':
     await button('Bold');await button('Italic');await expect(page.locator('#selection-value')).to_have_text('bold, italic');await check(True,'button group multiple selection')
    elif slug=='segmented':
     await page.get_by_text('Express',exact=True).click();await expect(page.locator('#selection-value')).to_have_text('express');await expect(page.get_by_role('radio',name='Express',exact=True)).to_be_focused();await check(True,'segmented pointer selection retains keyboard focus')
     for key,value,label in [('ArrowRight','pickup','Pickup'),('ArrowLeft','express','Express'),('ArrowLeft','standard','Standard'),('ArrowRight','express','Express'),('ArrowDown','pickup','Pickup'),('ArrowUp','express','Express'),('ArrowDown','pickup','Pickup')]:
      await page.keyboard.press(key);await expect(page.locator('#selection-value')).to_have_text(value);await expect(page.get_by_role('radio',name=label,exact=True)).to_be_focused()
     await check(True,'segmented arrow keys update focus and selection across all three choices')
     await page.get_by_role('button',name='All control benches',exact=True).focus();await page.get_by_text('Pickup',exact=True).click();await expect(page.get_by_role('radio',name='Pickup',exact=True)).to_be_focused();await page.keyboard.press('ArrowLeft');await expect(page.locator('#selection-value')).to_have_text('express');await page.keyboard.press('ArrowRight');await expect(page.locator('#selection-value')).to_have_text('pickup');await check(True,'clicking the selected segment restores focus for arrow keys')
     await page.get_by_role('button',name='All control benches',exact=True).focus();await page.keyboard.press('Tab');await expect(page.get_by_role('radio',name='Pickup',exact=True)).to_be_focused();await page.keyboard.press('ArrowLeft');await expect(page.locator('#selection-value')).to_have_text('express');await check(True,'Tab enters selected segment and arrow selection remains usable')
     await page.get_by_role('radio',name='Standard',exact=True).focus();await page.keyboard.press('Space');await expect(page.locator('#selection-value')).to_have_text('standard');await expect(page.get_by_role('radio',name='Standard',exact=True)).to_be_focused();await check(True,'Space selects the focused segment')
     await page.goto(sys.argv[1]+'/radio-keyboard-cases');group=page.get_by_role('radiogroup',name='Unselected segments',exact=True);await group.wait_for();await page.wait_for_timeout(250)
     await page.get_by_role('button',name='Before unselected group',exact=True).focus();await page.keyboard.press('Tab');await expect(group.get_by_role('radio',name='First',exact=True)).to_be_focused();await expect(page.locator('#unselected-value')).to_have_text('');await page.keyboard.press('Space');await expect(page.locator('#unselected-value')).to_have_text('first');await page.keyboard.press('ArrowDown');await expect(page.locator('#unselected-value')).to_have_text('last');await expect(group.get_by_role('radio',name='Last',exact=True)).to_be_focused();await page.keyboard.press('ArrowUp');await expect(page.locator('#unselected-value')).to_have_text('first');await check(True,'vertical segments enter the first enabled choice and skip disabled choices')
     await group.get_by_text('Unavailable middle',exact=True).click(force=True);await expect(page.locator('#unselected-value')).to_have_text('first');await expect(group.get_by_role('radio',name='Unavailable middle',exact=True)).to_be_disabled();await check(True,'disabled segment cannot take selection')
     await page.get_by_role('button',name='Before disabled group',exact=True).focus();await page.keyboard.press('Tab');await expect(page.get_by_role('button',name='After disabled group',exact=True)).to_be_focused();await check(True,'Tab skips a disabled segmented group')
    elif slug=='chip':
     await button('Important');await expect(page.locator('#selection-value')).to_have_text('True');await button('Remove Important');await expect(page.get_by_role('button',name='Important',exact=True)).to_have_count(0);await check(True,'chip toggle and removal')
    elif slug=='chip-list':
     await button('Review');await button('Remove Review');await expect(page.locator('#selection-value')).to_have_text('');await check(True,'chip removal clears selection')
    elif slug=='avatar':
     await expect(page.get_by_role('img',name='Fallback avatar')).to_contain_text('QR');await check(True,'avatar fallback')
    elif slug=='floating-label':
     await page.get_by_role('textbox',name='Full name').fill('Avery');await page.get_by_role('textbox',name='Full name').press('Tab');await expect(page.locator('.fx-floating-label')).to_have_class(re.compile('.*has-value.*'));await check(True,'floating label remains raised')
    elif slug=='stack-layout':
     await page.get_by_role('checkbox',name='Vertical layout').check();await expect(page.locator('.fx-stack-layout')).to_have_attribute('style',re.compile('.*flex-direction:column.*'));await check(True,'stack direction changes')
    elif slug=='animation-container':
     await button('Show content');await expect(page.locator('.fx-animation-container')).to_have_attribute('aria-hidden','true');await check(await page.locator('.fx-animation-container').get_attribute('inert') is not None,'hidden animation is inert')
    elif slug=='media-query':
     await expect(page.get_by_text('Wide viewport',exact=True)).to_be_visible();await page.set_viewport_size({'width':700,'height':900});await expect(page.get_by_text('Narrow viewport',exact=True)).to_be_visible();await page.set_viewport_size({'width':1500,'height':1050});await check(True,'media query follows viewport')
    elif slug=='loader':
     await check(await page.locator('.fx-loader').count()==3,'loader variants');await page.get_by_role('checkbox',name='Show loaders').uncheck();await expect(page.locator('.fx-loader')).to_have_count(0)
    elif slug=='loader-container':
     await button('Load for one second');await expect(page.locator('.fx-loader-container')).to_have_attribute('aria-busy','true');await status('Loading complete');await check(True,'loader container releases content')
    elif slug=='chunk-progress-bar':
     await page.get_by_role('slider',name='Progress value').fill('80');await expect(page.get_by_role('progressbar')).to_have_attribute('aria-valuenow','80');await check(True,'chunk progress value')
    elif slug=='masked-text-box':
     field=page.get_by_role('textbox',name='Phone number');await field.fill('2125550100');await field.press('Enter');await expect(page.locator('#mask-value')).to_have_text('(212) 555-0100');await field.fill('abc');await field.press('Enter');await expect(field).to_have_value('abc');await expect(page.get_by_role('alert')).to_be_visible();await check(True,'mask format and invalid draft')
    elif slug=='range-slider':
     await page.get_by_role('slider',name='Range start').fill('95');await expect(page.locator('#range-value')).to_have_text('80 – 80');await check(True,'range endpoints cannot cross')
    elif slug=='date-range-picker':
     await button('Choose range');await expect(page.get_by_role('dialog',name='Choose date range')).to_be_visible();await check(await page.locator('.fx-date-range input[type=date],.fx-date-range select').count()==0,'date range uses FlexCore editors')
    elif slug=='drop-down-tree':
     await page.get_by_role('button',name='Select a node… ▾').click();await page.get_by_text('West region',exact=True).click();await expect(page.locator('#tree-value')).to_have_text('west');await check(True,'tree leaf commits')
    elif slug=='color-gradient':
     await expect(page.locator('.fx-color-channel-value').first).to_have_text('212°')
     if os.environ.get('FLEXCORE_COLOR_SCREENSHOT'):await page.locator('.fx-color-gradient').screenshot(path=os.environ['FLEXCORE_COLOR_SCREENSHOT'])
     field=page.get_by_role('textbox',name='Hex color');await field.fill('#FF000080');await field.press('Tab');await expect(page.locator('#color-value')).to_have_text('#FF000080');await check(True,'gradient alpha color')
     hue=page.get_by_role('slider',name='Hue',exact=True)
     for degrees,color in [(60,'#FFFF0080'),(120,'#00FF0080'),(180,'#00FFFF80'),(240,'#0000FF80'),(300,'#FF00FF80'),(0,'#FF000080')]:
      await hue.fill(str(degrees));await expect(page.locator('#color-value')).to_have_text(color);await expect(field).to_have_value(color)
      red,green,blue=bytes.fromhex(color[1:7]);await expect(page.locator('.fx-hsv-surface')).to_have_css('background-color',f'rgb({red}, {green}, {blue})');await expect(page.locator('.fx-color-preview span')).to_have_css('background-color',re.compile(rf'rgba\({red}, {green}, {blue}, 0\.50?2?\)'))
     await check(True,'hue selects yellow, green, cyan, blue, magenta, and red with alpha preserved')
     track=await hue.evaluate('e=>getComputedStyle(e).backgroundImage')
     await check(all(color in track for color in ['rgb(255, 0, 0)','rgb(255, 255, 0)','rgb(0, 255, 0)','rgb(0, 255, 255)','rgb(0, 0, 255)','rgb(255, 0, 255)']),'hue slider visibly renders the full spectrum')
     await hue.press('End');await expect(hue).to_have_value('360');await expect(page.locator('#color-value')).to_have_text('#FF000080');await hue.press('Home');await hue.press('ArrowRight');await expect(page.locator('#color-value')).to_have_text('#FF040080');await check(True,'hue keyboard endpoints and arrow adjustment')
     await hue.fill('120');await expect(page.locator('#color-value')).to_have_text('#00FF0080')
     surface=page.locator('.fx-hsv-surface');box=await surface.bounding_box();await surface.click(position={'x':box['width']/2,'y':box['height']/2});await expect(page.locator('#color-value')).to_have_text('#40804080');await expect(page.get_by_role('slider',name='Saturation',exact=True)).to_have_attribute('aria-valuetext','50%');await expect(page.get_by_role('slider',name='Brightness',exact=True)).to_have_attribute('aria-valuetext','50%');await check(True,'surface selects saturation and brightness at the pointer position')
     await page.get_by_role('slider',name='Opacity',exact=True).fill('100');await expect(page.locator('#color-value')).to_have_text('#408040');await page.get_by_role('slider',name='Saturation',exact=True).fill('100');await expect(page.locator('#color-value')).to_have_text('#008000')
     brightness=page.get_by_role('slider',name='Brightness',exact=True);await brightness.fill('0');await expect(page.locator('#color-value')).to_have_text('#000000');await hue.fill('60');await expect(hue).to_have_attribute('aria-valuetext','60°');await brightness.fill('100');await expect(page.locator('#color-value')).to_have_text('#FFFF00');await check(True,'hue changes while black survive restoring brightness')
    elif slug=='flat-color-picker':
     field=page.get_by_role('textbox',name='Hex color');await field.fill('#FF0000');await field.press('Tab');await page.get_by_role('slider',name='Hue',exact=True).fill('120');await expect(field).to_have_value('#00FF00');await expect(page.locator('.fx-hsv-surface')).to_have_css('background-color','rgb(0, 255, 0)');await expect(page.locator('#color-value')).to_have_text('#3478C5');await button('Cancel');await expect(field).to_have_value('#3478C5');await check(True,'flat picker shares hue selection and cancels its draft')
     await button('Palette');await page.get_by_role('option',name='#FF0000',exact=True).click();await expect(page.locator('#color-value')).to_have_text('#3478C5');await button('Apply color');await expect(page.locator('#color-value')).to_have_text('#FF0000');await check(True,'color draft applies')
    elif slug=='signature':
     box=await page.locator('.fx-signature svg').bounding_box();await page.mouse.move(box['x']+30,box['y']+30);await page.mouse.down();await page.mouse.move(box['x']+180,box['y']+80,steps=10);await page.mouse.up();await expect(page.locator('#signature-value')).to_have_text('Strokes: 1')
     async with page.expect_download() as pending:await button('Download signature')
     await (await pending.value).save_as('signature.svg');await check(len(ET.parse('signature.svg').getroot().findall('{http://www.w3.org/2000/svg}polyline'))==1,'signature SVG contains real stroke');await button('Undo stroke');await expect(page.locator('#signature-value')).to_have_text('Strokes: 0')
    elif slug=='upload':
     await page.locator('[data-fx-upload-active=true] input[type=file]').set_input_files({'name':'sample.txt','mimeType':'text/plain','buffer':b'x'*18000});await status('Received sample.txt: 18000 bytes');await page.locator('[data-fx-upload-active=true] input[type=file]').set_input_files({'name':'second.txt','mimeType':'text/plain','buffer':b'second'});await status('Received second.txt: 6 bytes');await check(True,'upload chunks and multiple selections')
     await page.locator('[data-fx-upload-active=true] input[type=file]').set_input_files({'name':'cancel.txt','mimeType':'text/plain','buffer':b'x'*200000});await button('Cancel upload');await expect(page.locator('.fx-upload-list')).to_contain_text('Cancelled');await button('Retry upload');await status('Received cancel.txt: 200000 bytes');await check(True,'cancelled upload restarts from offset zero')
     await page.locator('[data-fx-upload-active=true] input[type=file]').set_input_files({'name':'blocked.exe','mimeType':'application/octet-stream','buffer':b'not allowed'});await expect(page.locator('.fx-upload-list')).to_contain_text('This file extension is not allowed.');await check(True,'upload rejects disallowed file type')
    elif slug=='drop-zone':
     transfer=await page.evaluate_handle("()=>{const d=new DataTransfer();d.items.add(new File(['drop test'],'dropped.txt',{type:'text/plain'}));return d;}");await page.locator('.fx-drop-zone').dispatch_event('drop',{'dataTransfer':transfer});await status('Received dropped.txt');await check(True,'external drop transfers browser files')
    elif slug=='prompt-box':
     field=page.get_by_role('textbox',name='Message');await field.fill('Hello prompt');await field.press('Enter');await status('Sent: Hello prompt (0 attachments)');await check(True,'prompt Enter sends')
    elif slug=='chat':
     await page.get_by_role('textbox',name='Message').fill('Hello chat');await button('Send');await expect(page.get_by_role('log')).to_contain_text('Local demo echo: Hello chat');await check(True,'chat provider reply')
    elif slug=='inline-ai-prompt':
     await button('Rewrite selection');await button('Generate');await button('Use response');await expect(page.locator('#inline-text')).to_have_text('Local demo rewrite: concise paragraph.');await check(True,'inline AI applies')
    elif slug=='smart-paste-button':
     await page.evaluate("text=>navigator.clipboard.writeText(text)","Name: Taylor\nQuantity: 7");await button('Smart paste');dialog=page.get_by_role('dialog',name='Review pasted fields');await dialog.wait_for();await page.wait_for_timeout(250)
     if await dialog.get_by_role('textbox',name='Text to map to fields').count():await dialog.get_by_role('textbox',name='Text to map to fields').fill('Name: Taylor\nQuantity: 7');await dialog.get_by_role('button',name='Map fields').click()
     await dialog.get_by_role('button',name='Apply fields').click();await status('Applied 2 fields');await expect(page.get_by_role('textbox',name='Name',exact=True)).to_have_value('Taylor');await expect(page.get_by_role('textbox',name='Quantity',exact=True)).to_have_value('7');await check(True,'smart paste applies typed values')
    elif slug=='speech-to-text-button':
     await page.evaluate("""()=>{window.SpeechRecognition=class {start(){setTimeout(()=>{this.onresult?.({resultIndex:0,results:[{0:{transcript:'Recognized speech'},isFinal:true}]});this.onend?.();},80);}abort(){}stop(){this.onend?.();}};}""")
     await button('Speak');await expect(page.get_by_role('textbox',name='Transcription')).to_have_value('Recognized speech');await check(True,'speech event bridge commits simulated recognition')
    elif slug=='spreadsheet':
     await page.get_by_role('gridcell',name='B2',exact=True).dblclick();field=page.get_by_role('textbox',name='Edit cell',exact=True);await field.fill('5');await field.press('Enter');await expect(page.get_by_role('gridcell',name='D2',exact=True)).to_have_text('62.5');await button('Undo');await expect(page.get_by_role('gridcell',name='D2',exact=True)).to_have_text('25');await button('Redo');await expect(page.get_by_role('gridcell',name='D2',exact=True)).to_have_text('62.5')
     async with page.expect_download() as pending:await button('Save workbook')
     await (await pending.value).save_as('verified-workbook.xlsx');book=openpyxl.load_workbook('verified-workbook.xlsx',data_only=False);await check(book.active['B2'].value==5 and book.active['D2'].value=='=B2*C2','independent XLSX reader verifies value and formula')
     await expect(page.get_by_role('textbox',name='Edit cell',exact=True)).to_have_count(0);await page.get_by_role('gridcell',name='B2',exact=True).click();await page.get_by_role('gridcell',name='B2',exact=True).press('ArrowRight');await expect(page.get_by_role('gridcell',name='C2',exact=True)).to_be_focused();await page.get_by_role('gridcell',name='C2',exact=True).press('Shift+ArrowDown');await page.get_by_role('gridcell',name='C3',exact=True).press('Shift+ArrowDown');await expect(page.get_by_role('textbox',name='Cell address')).to_have_value('C2:C4');await check(True,'spreadsheet arrows move one focus and extend selection')
    elif slug=='scheduler':
     await page.get_by_role('button',name=re.compile('Design review')).first.click();dialog=page.get_by_role('dialog',name='Edit appointment');await dialog.get_by_role('textbox',name='Title',exact=True).fill('Design review updated');await dialog.get_by_role('button',name='Save appointment').click();await expect(dialog).to_have_count(0);await expect(page.get_by_role('button',name=re.compile('Design review updated')).first).to_be_visible();await check(True,'scheduler dialog commit')
     await page.get_by_role('button',name=re.compile('Daily standup')).first.click();dialog=page.get_by_role('dialog',name='Edit appointment');await dialog.get_by_role('textbox',name='Title',exact=True).fill('One occurrence only');await dialog.get_by_role('button',name='Save appointment').click();await expect(dialog).to_have_count(0);await expect(page.get_by_role('button',name=re.compile('One occurrence only')).first).to_be_visible();await check(await page.get_by_role('button',name=re.compile('Daily standup')).count()==4,'recurrence edit changes one occurrence')
    elif slug=='map':
     await button('Zoom in');await expect(page.locator('#bench-status')).to_contain_text('Zoom 4');await button('Fit markers');await button('New York');await status('New York');await check(True,'map zoom and marker binding')
   await check(not errors,'no browser or Blazor errors: '+str(errors));print(f'All {checks} remaining-control browser checks passed.',flush=True)
  except:
   print('ERRORS',errors,flush=True);print((await page.locator('body').inner_text())[-5000:],flush=True);await page.screenshot(path='remaining-controls-failure.png',full_page=True);raise
  finally:await browser.close()
asyncio.run(main())
