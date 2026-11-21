const fs=require('node:fs');
const {pathToFileURL}=require('node:url');
const {chromium}=require('playwright');

async function main(){
  const [report,dir,browserPath]=process.argv.slice(2);
  const browser=await chromium.launch({headless:true,executablePath:browserPath,args:['--disable-background-networking']});
  try{
    const page=await browser.newPage({viewport:{width:1040,height:930},offline:true});
    const errors=[];page.on('pageerror',err=>errors.push(err.message));
    await page.goto(pathToFileURL(report).href);
    if(await page.locator('main table tbody tr').count()!==9)throw new Error('Incomplete comparison');
    const stats=await page.locator('.stats strong').allTextContents();
    await page.screenshot({path:dir+'/report-preview.png'});
    await page.locator('.case summary').first().click();
    if(!await page.locator('details[open] pre').first().isVisible())throw new Error('Hidden test evidence');
    await page.locator('#filter').click();
    if(await page.locator('.case.ok:visible').count())throw new Error('Filter did not hide passing checks');
    await page.locator('#filter').click();await page.setViewportSize({width:390,height:840});
    if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)||errors.length)throw new Error('Layout or script error');
    fs.writeFileSync(dir+'/report-verification.json',JSON.stringify({stats,offline:true,groups:9,details:true,filter:true,mobile:true}));
    console.log(stats.join(', '));
  }finally{await browser.close();}
}
main().catch(err=>{console.error(err);process.exitCode=1;});
