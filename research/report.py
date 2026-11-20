"""Build an offline report and publish only public observations or aggregate private scores."""
import argparse
from collections import Counter
from datetime import datetime
from html import escape
import hashlib
import json
import math
import os
from pathlib import Path
from statistics import median,quantiles
import zipfile


ROOT=Path(__file__).resolve().parents[1]
PUBLIC_WEIGHT='general-evidence.gguf'
def read(p):
    return json.loads(p.read_text(encoding='utf-8-sig'))
def put(p,data):
    p.write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf8')
def sha(p):
    with p.open('rb') as f:
        return hashlib.file_digest(f,'sha256').hexdigest()
def fmt(v,places=2):
    return f'{v:.{places}f}'.replace('.',',')
def fraction(rows):
    return sum(bool(r['correct']) for r in rows),len(rows)
def groups(rows,key='kind'):
    return {k:fraction([r for r in rows if r[key]==k]) for k in sorted({r[key] for r in rows})}
def paired(before,after):
    assert len(before)==len(after)
    wins=sum(b and not a for a,b in zip(before,after))
    losses=sum(a and not b for a,b in zip(before,after))
    d=wins+losses
    p=min(1,2*sum(math.comb(d,i) for i in range(min(wins,losses)+1))/2**d) if d else 1
    return dict(wins=wins,losses=losses,exact_p=p)
def wilson(k,n):
    z=1.959963984540054
    a=k/n;den=1+z*z/n;mean=(a+z*z/(2*n))/den
    width=z*math.sqrt((a*(1-a)+z*z/(4*n))/n)/den
    return [100*(mean-width),100*(mean+width)]
def row_delta(name,b,a,n):
    return [name,f'{b}/{n}',f'{a}/{n}',f'{a-b:+d} · {fmt(100*(a-b)/n,1)} п. п.',
            fmt(100*(a-b)/b,1)+' %' if b else '—']
def metric_row(name,b,a,digits=2):
    return [name,fmt(b,digits),fmt(a,digits),fmt(a-b,digits),fmt(100*(a-b)/b,1)+' %' if b else '—']
def table(headers,rows):
    head=''.join('<th scope="col">'+escape(str(x))+'</th>' for x in headers)
    body=''.join('<tr>'+''.join('<td>'+escape(str(x))+'</td>' for x in row)+'</tr>' for row in rows)
    return '<div class="scroll"><table><thead><tr>'+head+'</tr></thead><tbody>'+body+'</tbody></table></div>'
def par(value):
    return '<p>'+value+'</p>'
def heading(value):
    return '<h3>'+escape(value)+'</h3>'
def link(label,url):
    return '<a href="'+escape(url,quote=True)+'">'+escape(label)+'</a>'
def get_runtime(report):
    return {str(load):{key:row[key] for key in ('requested','errors','aggregate_tps','ttft','latency','tps','resources')} for load in (1,2,4) for row in [report['concurrency_'+str(load)]]}
def time_sum(p):
    rows=read(p);return sum(r['latency'] for r in rows)
def log_elapsed(p):
    import re
    seconds=0
    for line in p.read_text(encoding='utf8',errors='replace').splitlines():
        match=re.match(r'(\d+)\.(\d+)\.(\d+)\.(\d+)\s',line)
        if match:
            mins,sec,ms,us=map(int,match.groups());seconds=max(seconds,mins*60+sec+ms*.001+us*.000001)
    return seconds


def compile_report(base,dist):
    original=read(base/'baseline/baseline.json');meta=read(base/'baseline/gguf.json');sources=read(base/'sources/sources.json')
    installed=read(ROOT/'.build/installed-v33.json')
    finals=read(base/'release.json');accepted=finals['general_evidence'];kv_adopted=finals['kv8']
    apps=[read(base/(v+'/application.json')) for v in ('app-before','app-final')]
    registry=[read(base/(v+'/application.json')) for v in ('registry-before','registry-final')]
    before,after=[groups(a) for a in apps]
    raw,current,final,kv=[read(base/(v+'/results.json')) for v in ('raw','current','final','kv8')]
    network=[get_runtime(x) for x in (current,final,kv)]
    e0,e1=[read(base/('evidence/'+v+'-closed.json')) for v in ('baseline','final')]
    dev=read(base/'evidence/candidate.json');e0s=e0['score'];e1s=e1['score']
    index={r['id']:r for r in e1['observations']};ordered=[index[r['id']] for r in e0['observations']]
    significance=paired([r['predicted']==r['expected'] for r in e0['observations']],[r['predicted']==r['expected'] for r in ordered])
    private=read(base/'private-check/aggregate.json');private_delta=paired([r['raw']==r['expected'] for r in private['observations']],[r['installed']==r['expected'] for r in private['observations']])
    business=read(base/'legacy-regressions/results.json')
    math0=read(base/'math/baseline.json');math1=read(base/'math/develop.json')['development']
    rtypes=('Факты по памяти','JSON по строке реестра','Извлечение поля из реестра')
    reference=[[fraction(r[j:j+4]) for j in (0,4,8)] for r in registry]
    guards=read(base/'guard.json')
    appmetrics={'arithmetic':'numeric','history':'history','json':'json','quotes':'quote','mixed':'mixed','invalid':'invalid'}
    appstat={k:dict(before=before[v],after=after[v]) for k,v in appmetrics.items()}
    calc=[median(r['latency'] for r in rows if r['kind']=='numeric') for rows in apps]
    per_class1=[row['correct'] for row in e1s['per_class']]
    matched={r['id']:r['correct'] for r in current['quality']};kvquality=read(base/'kv8-matched/results.json')['quality']
    kvbase=sum(matched[r['id']] for r in kvquality);kvfinal=sum(r['correct'] for r in kvquality)
    free=read(base/'free/free-generation.json')
    cost={"public_evidence_native_seconds":round(sum(log_elapsed(p) for p in (base/'evidence').glob('*.log')),1),
          "wpf_before_seconds":round(time_sum(base/'app-before/application.json'),1),"wpf_after_seconds":round(time_sum(base/'app-final/application.json'),1),
          "math_baseline_seconds":round(sum(r['seconds'] for v in math0.values() for r in v['observations']),1),
          "math_thinking_development_seconds":round(sum(r['seconds'] for r in math1['observations']),1)}
    metrics=dict(version=installed['version'],seed=20261120,date='2026-11-20',release=finals,original=original,
                 architecture=meta,sources=sources,evidence=dict(before=e0s,candidate=e1s,development=dev,paired=significance,wilson_before=wilson(e0s['correct'],e0s['n']),wilson_after=wilson(e1s['correct'],e1s['n'])),
                 private_formats=dict(count=private['total'],scores=private['scores'],sha256=private['sha256'],paired_raw_vs_deployed=private_delta),
                 mathematics={k:{h:v[h] for h in ('total','correct','p50','p95')} for k,v in {**math0,'thinking_dev':math1}.items()},
                 application=appstat,calculator_seconds_p50=calc,iana={name:[reference[0][i],reference[1][i]] for i,name in enumerate(rtypes)},
                 diagnostics={v['variant']:groups(v['quality']) for v in (raw,current,final)},native_performance=dict(before=network[0],after=network[1],kv8=network[2]),
                 kv8=dict(matched_before=kvbase,matched_candidate=kvfinal,total=len(kvquality)),controls=guards,
                 legacy={k:v for k,v in business.items() if 'observations' not in k},generation=free,cost=cost,installation=installed)
    ts={s:read(base/('transfer/'+s+'.json')) for s in ('raw-external','grammar-external','candidate-external','candidate-regression')}
    def correct_seq(s):return [v['predicted']==v['expected'] for v in ts[s]['observations']]
    metrics['transfer']={k:v['score'] for k,v in ts.items()}
    metrics['transfer'].update(candidate=read(base/'transfer/candidate.json'),
                               paired_original=paired(correct_seq('raw-external'),correct_seq('candidate-external')),
                               paired_grammar=paired(correct_seq('grammar-external'),correct_seq('candidate-external')))
    metrics['sourcecheck_window']=dict(zip(('matches_native','total'),fraction(read(base/'evidence-window/application.json'))))
    metrics['standalone']=read(ROOT/'.build/standalone33.json')
    metrics['deployed_rcb']=read(base/'deployed-evidence.json')['score']
    metrics['installed_app']=groups(read(base/'app-installed/application.json'))
    ir=read(base/'registry-installed/application.json')
    metrics['installed_registry']=[fraction(ir[j:j+4]) for j in (0,4,8)]
    import re
    ui=read(base/'ui-observations.json')
    studio=(base/'studio-check.txt').read_text(encoding='utf-8-sig')
    ui['verified']=studio.strip().endswith('PASS')
    ui['parallel_seconds']=float(re.search(r'overlap seconds ([\d.,]+)',studio)[1].replace(',','.'))
    metrics['studio']=ui
    metrics['voice']=read(base/'voice-installed/result.json')
    appdir=Path(os.environ['LOCALAPPDATA'])/'Programs/Ostum Desktop'
    paths=[appdir/'OstumDesktop.exe',ROOT/'dist/Sfera-Setup.msi',ROOT/'dist/Sfera-Install.exe']+sorted((appdir/'adapters').glob('*.gguf'))
    metrics['release_files']={p.name:dict(bytes=p.stat().st_size,sha256=sha(p)) for p in paths}
    dist.mkdir(exist_ok=True,parents=True)
    put(dist/'Результаты.json',metrics)
    return render(base,dist,metrics,apps,registry,network,reference)


def render(base,dist,m,apps,registry,perf,reference):
    ev=m['evidence'];old=ev['before'];new=ev['candidate'];decision=m['release']
    privacy=m['private_formats'];maths=m['mathematics'];origin=m['original']['files']
    delta=ev['paired'];calc=m['calculator_seconds_p50'];arch=m['architecture']
    before,after=[groups(rows) for rows in apps];mixed_ok=after['mixed']==before['mixed']
    alpha=ev['development']['fit'];seconds=m['cost']
    def section(i,title,body):
        return '<section id="'+i+'"><h2><span>'+i+'</span> '+title+'</h2>'+body+'</section>'
    def desc(f):return f"{f['correct']}/{f['n']}"
    def verdict(v):return 'Принято в 3.3' if v else 'Оставлено в эксперименте'
    def pct(values):return '–'.join(fmt(v,1) for v in values)+' %'
    qwen=link('Карточка Qwen3.5-9B','https://huggingface.co/Qwen/Qwen3.5-9B')
    config=link('Конфигурация этой ревизии',m['sources']['qwen-config.json']['url'])
    llama=link('llama.cpp b12547','https://github.com/ggml-org/llama.cpp/releases/tag/b12547')
    rcb=link('RussianSuperGLUE · Russian Commitment Bank','https://huggingface.co/datasets/RussianNLP/russian_super_glue')
    mgsm=link('Google Research · MGSM-Rev2','https://github.com/google-research-datasets/MGSM-Rev2')
    iana=link('IANA · HTTP Status Code Registry','https://www.iana.org/assignments/http-status-codes/')
    installed=m['installation']
    plain=par('Исследование выполнено 20 ноября 2026 года на компьютере пользователя. Отправная точка — установленная «Сфера» 3.2.1. В обеих версиях используются те же 5 680 522 464 байта базовых весов Qwen3.5-9B Q4_K_M. Работа шла на одной RTX 4050 Laptop, 6 141 МиБ, с драйвером 592.00, процессором Ryzen 7 7435HS и 16 ГБ системной памяти. Модели и пользовательские данные обрабатывались локально. Платные API не использовались.')
    audit=heading('Что доступно для изменения')+par('На диске доступны квантизованные открытые веса, отдельный визуальный проектор, шаблон диалога, исходный код приложения и открытый движок. Замороженный трансформер можно дополнять весами LoRA в формате GGUF. На этом ПК для эксперимента использовали выходной слой. Исходные данные предобучения Qwen и веса ChatGPT недоступны.')
    audit+=table(['Компонент','Проверенная конфигурация'],[
        ['Языковая сеть',format(arch["stored_tensor_elements"], ",").replace(",", " ")+' элементов в 427 тензорах GGUF. Плотная сеть из 32 блоков. 24 блока линейного внимания Gated DeltaNet, 8 блоков полного внимания. Скрытая ширина 4 096, FFN 12 288, 16 голов внимания и 4 головы KV в блоках полного внимания.'],
        ['Зрение','Отдельный F16-проектор размером 918 166 080 байт. В официальной конфигурации визуального кодера 27 блоков, ширина 1 152 и 16 голов. Создание картинок выполняют отдельные SDXS и Z-Image-Turbo.'],
        ['Токенизация','BPE семейства GPT-2 с предобработкой qwen35. 248 320 токенов, 247 587 слияний. В локальном GGUF EOS 248046 и PAD 248055. Проверен встроенный Jinja-шаблон длиной '+str(arch['tokenizer.chat_template']['characters'])+' символов, включая системную роль, инструменты и переключатель обдумывания.'],
        ['Контекст','Заявлено 262 144 токена. Приложение выделяет 8 192 для одного диалога или два слота по 4 096. При 632, 2 682 и 6 737 входных токенах модель вернула шифр в шести из шести диагностик. Паспортная длина на ноутбуке не проверялась.'],
        ['Генерация','Обычный диалог использует temperature 0,7, top-p 0,8, top-k 20, presence penalty 1,5. Ручное обдумывание использует 1,0 и top-p 0,95. Максимум обычного ответа 1 200 токенов, ручного обдумывания 2 048. Проверка тезиса и диагностические измерения — жадное декодирование.'],
        ['Исполнение','Windows x64, локальный llama.cpp b12547 по защищённому ключом loopback, CUDA 12.4, запас размещения 1 024 МиБ, пакет 512 и микропакет 128. Изображение может переместить проектор на CPU. Встроено переключение на Vulkan и CPU.'],
        ['Лицензии','Qwen — Apache 2.0, llama.cpp и публикация RussianSuperGLUE — MIT. У дополнительных графических моделей и библиотек NVIDIA свои лицензии. Сведения и контрольные суммы доступны в дистрибутиве.']])
    audit+=par(qwen+' · '+config+' · '+llama)
    audit+=heading('Почему возникали ошибки')+par('Из 44 нейтральных тезисов в новой закрытой выборке исходная сеть не распознала ни одного. Формат свободного декодирования допускал Markdown вокруг JSON. В приложении модель маршрутизации иногда передавала буквальный запрос на JSON нескольким агентам или запускала генератор изображения при вопросе о прошлом чата. Построитель контекста удалял все реплики старше двадцати даже при совпадении названия проекта. Чистые выражения уже умел вычислять локальный парсер, однако после расчёта приложение ещё ожидало генерацию объяснения.')
    audit+=par('Слабость рассуждений проверялась отдельно. Точное совпадение с эталоном получилось в '+str(maths['closed_ru']['correct'])+' из 20 закрытых русских и в '+str(maths['closed_en']['correct'])+' из 4 самостоятельных английских задач MGSM-Rev2. Английские задачи имеют другие номера. Эти четыре ответа не дают надёжной оценки различия языков. '+mgsm)
    evidence_status='Отклонено · +4 при пороге +5';transfer=m['transfer']
    costs=lambda n:fmt(seconds[n]/60,1)+' мин'
    cards=[
        ['1 · Калькулятор','Передать законченную формулу существующему decimal-парсеру. Защита от перехвата перевода, объяснения и даты.','6 выражений, 3 смешанных запроса, деление на ноль. Медиана '+fmt(calc[0],3)+' → '+fmt(calc[1],3)+' с.','Принято. Откат через прежний обработчик запроса.'],
        ['2 · Память чата','По явной отсылке найти старые реплики того же чата по редким словам. Совпадение имени проекта обязательное, если оно выделено.','До двух отрывков до 390 знаков, роли сохраняются как подписи цитат. Пять закрытых задач, отдельные проверки чужого названия и длинной реплики.','Принято. Откат через прежний построитель контекста.'],
        ['3 · Формат и поля','При явном запросе объекта включить грамматику JSON. Поле с однозначной записью key = value извлечь буквально. В сложной записи ограничить ответ модели предоставленным материалом.','4 объекта и 4 поля в окне, ещё 8 строк внешнего реестра. Значения и типы сравниваются с эталоном.','Принято. Откат настроек формата и обработчика выделенного поля.'],
        ['4 · Веса Qwen','Обучить ранговое дополнение выходной проекции для общего русского вывода по источнику. 339 обучающих и 48 валидационных примеров RCB.','Native dev 25/48 → '+desc(ev['development']['native_development'])+', закрытый тест '+desc(old)+' → '+desc(new)+'. Сумма длительностей зафиксированных запусков движка около '+costs('public_evidence_native_seconds')+'.',evidence_status+'. Откат через предыдущую версию или отключение этой специализации.'],
        ['5 · KV-кеш Q8','Проверить, освобождает ли Q8 с flash attention достаточно видеопамяти на двух слотах. Риск — другой баланс выгрузки слоёв и замедление.','При двух клиентах '+fmt(perf[0]['2']['aggregate_tps'])+' → '+fmt(perf[2]['2']['aggregate_tps'])+' токена/с. Пиковая память платы '+fmt(perf[0]['2']['resources']['peak_gpu_board_mib'],0)+' → '+fmt(perf[2]['2']['resources']['peak_gpu_board_mib'],0)+' МиБ.',verdict(decision['kv8'])+'. Контрольная конфигурация снимает флаги Q8.'],
        ['6 · Обдумывание','Сравнить флаг thinking при одинаковых 512 выходных токенах, нулевой температуре и одинаковых условиях. Условие автопереключения — минимум +2 из 8 на разработке.','MGSM-Rev2, разработка '+str(maths['development']['correct'])+'/8 → '+str(maths['thinking_dev']['correct'])+'/8. p95 '+fmt(maths['development']['p95'],1)+' → '+fmt(maths['thinking_dev']['p95'],1)+' с. Сумма времени ответов с обдумыванием '+costs('math_thinking_development_seconds')+'.','Автовключение отклонено. Пользователю доступен прежний ручной режим.']]
    cards.append(['7 · Дискриминативная проверка','Повторно использовать только 339 обучающих и 48 валидационных примеров RCB. Обучить логистический классификатор на признаках Qwen и разрешить три варианта вердикта при декодировании.','Новый закрытый XNLI, 90 русских пар. Исходная сеть '+desc(transfer['raw-external'])+', только грамматика '+desc(transfer['grammar-external'])+', грамматика с весами '+desc(transfer['candidate-external'])+'. RCB, теперь регрессионная панель, '+desc(transfer['candidate-regression'])+'.',verdict(decision['general_evidence'])+'. Откат к проверке общих текстов в 3.2.1.'])
    put(dist/'Журнал экспериментов.json',dict(priority=[dict(zip(('experiment','mechanism','measurement','decision_and_rollback'),r)) for r in cards],cost=seconds,protocol=read(ROOT/'research/protocol.json'),supplement=read(ROOT/'research/supplement.json'),transfer_protocol=read(ROOT/'research/transfer-protocol.json')))
    journal=table(['Приоритет и уровень','Изменение и риск','Проверка и затраты','Решение и откат'],cards)
    journal+=par('Каждый вариант тестировался на том же ноутбуке. Денежные расходы на API равны нулю. В таблице задержек приведена измеренная энергия видеоплаты для 12 ответов. Тариф электроэнергии и показания розетки неизвестны. Общую стоимость электричества не рассчитывали.')
    journal+=par('Базовая квантизация Q4_K_M сохранена. В предшествующем опыте спекулятивное n-gram-декодирование дало 16,46 токена/с против 16,84 у обычного CUDA. Повторно внедрять его оснований не появилось. Полное дообучение, DPO, дистилляцию и изменение архитектуры не проводили. Для широкого обучения нет достаточно разнообразной проверенной разметки. Только параметры 9B в BF16 занимают около 18 ГБ до градиентов и состояния оптимизатора.')
    journal+=par('В восьми задачах с включённым обдумыванием модель исчерпала лимит 512 токенов до финального численного ответа. Результат 0/8 относится к этому ограничению. В ручном режиме приложения доступен более длинный ответ. На закрытой математической части этот отклонённый режим повторно не запускался.')
    examples=[row_delta('Установленная 3.3 · общий русский вывод по материалу',old['correct'],m['deployed_rcb']['correct'],old['n']),
              row_delta('Эксперимент 1 · RCB, все классы',old['correct'],new['correct'],old['n']),
              metric_row('Эксперимент 1 · macro-F1',old['macro_f1'],new['macro_f1'],3)]
    for name,o,n in zip(('Следует из текста','Противоречит тексту','Недостаточно данных'),old['per_class'],new['per_class']):
        examples.append(row_delta('Эксперимент 1 · '+name,o['correct'],n['correct'],o['total']))
    for name,key in [('Формулы','numeric'),('Вопрос по старым репликам','history'),('Только JSON с верными значениями','json'),('Выделенное поле и посторонняя цитата','quote'),('Обычная просьба с числовым выражением','mixed'),('Деление на ноль','invalid')]:
        examples.append(row_delta('Окно чата · '+name,before[key][0],after[key][0],after[key][1]))
    examples.append(metric_row('Окно чата · медиана ответа на формулу, с',*calc,digits=3))
    for i,name in enumerate(('Фактический вопрос по памяти','JSON по предоставленной строке','Поле по предоставленной строке')):
        examples.append(row_delta('IANA · '+name,reference[0][i][0],reference[1][i][0],4))
    comp=table(['Проверка','До','После','Разница','Относительно исходного'],examples)
    comp+=par('Для первой строки повторены все 83 проверки с фактически установленными адаптерами. Строки «Эксперимент 1» относятся к первой обученной версии весов. Строки «Окно чата» и IANA сравнивают 3.2.1 и 3.3. Прочерк означает нулевой исходный результат. У RCB 95%-й интервал Уилсона для исходной точности '+pct(ev['wilson_before'])+', для первого кандидата '+pct(ev['wilson_after'])+'. В парном сравнении улучшились '+str(delta['wins'])+' ответа, ухудшились '+str(delta['losses'])+'. Двусторонний точный тест Мак-Немара p = '+fmt(delta['exact_p'],5)+'. В закрытом наборе всего два прямых противоречия, поэтому обобщать результат этого класса нельзя. '+rcb)
    comp+=par('Закрытые 83 фрагмента не участвовали в подборе весов и не входили в прежние локальные экзамены. Русские языковые данные и опубликованные математические задачи могли входить в исходное предобучение поставщика. Экзамен RCB оценивает первый вердикт модели. Фактическая полнота свободного объяснения отдельно по каждому примеру не размечалась.')
    comp+=heading('Вторая модель проверки и независимый XNLI')
    newx=transfer['candidate-external'];basex=transfer['raw-external'];grammar=transfer['grammar-external']
    comp+=table(['Проверка второго кандидата','До','После','Разница','Относительно исходного'],[
        row_delta('XNLI · исходная Qwen → калиброванные веса с грамматикой',basex['correct'],newx['correct'],90),
        row_delta('XNLI · та же грамматика без весов → с весами',grammar['correct'],newx['correct'],90),
        metric_row('XNLI · macro-F1',basex['macro_f1'],newx['macro_f1'],3),
        row_delta('RCB · прежняя тестовая панель',old['correct'],transfer['candidate-regression']['correct'],83)]+[
        row_delta('XNLI · '+title,b['correct'],v['correct'],30) for title,b,v in zip(('следует','противоречит','недостаточно данных'),basex['per_class'],newx['per_class'])])
    marker=transfer['paired_original']
    comp+=par('Второй протокол зафиксирован после отказа от первого кандидата и до измерений XNLI. Для обучения использованы только RCB. Девяносто новых русских пар из XNLI по 30 на класс открыты для итоговой оценки этого кандидата один раз. Разница с исходной сетью по парным наблюдениям — '+str(marker['wins'])+' улучшений, '+str(marker['losses'])+' ухудшений, точный p = '+fmt(marker['exact_p'],5)+'. XNLI (Conneau и соавторы, 2018) использован в локальном исследовании как внешний экзамен. Его тексты и обученные по нему веса не входят в поставку. '+link('Корпус XNLI','https://github.com/facebookresearch/XNLI')+' · '+link('Лицензия XNLI, CC BY-NC 4.0','https://github.com/facebookresearch/XNLI/blob/main/LICENSE'))
    comp+=par('Решение по весам для общих утверждений — '+verdict(decision['general_evidence'])+'. В реальном окне чата '+str(m['sourcecheck_window']['matches_native'])+' из '+str(m['sourcecheck_window']['total'])+' проверок дали тот же вердикт, что и соответствующий режим движка. Равенство этих ответов проверяет подключение выбранной конфигурации.')
    comp+=par('Проверки окна чата используют специально подготовленные примеры. Отдельные 12 вопросов IANA используют значения официального реестра и фиксированные шаблоны запроса. Это проверяемые внешние факты, выборка из одной области. '+iana)
    comp+=heading('Рабочие форматы и регрессии')
    pr=privacy['scores'];reg=m['legacy']
    comp+=par('На 30 реальных запросах из 30 отдельных прежних бесед по рабочему формату исходная Qwen получила '+str(pr['raw'])+'/30, предыдущий персональный адаптер '+str(pr['old'])+'/30, установленный '+str(pr['installed'])+'/30. Эти запросы отделены от подбора персональных весов и сгруппированы по беседам и близости текста. Разметка сделана при аудите по цели запроса. Парное сравнение исходной и установленной головы даёт p = '+fmt(privacy['paired_raw_vs_deployed']['exact_p'],4)+'. В релизе 3.3 персональные веса сохранены. Проверялся выбор формата, а не экспертная оценка всего сгенерированного документа.')
    comp+=par('Предыдущая работа с историей охватила 397 бесед ChatGPT и 1 200 доступных пользовательских сообщений. Персональный адаптер обучался на 82 обезличенных рабочих запросах и 80 закрепляющих примерах. Профиль учитывает работу с Gamma, Lovable, Яндекс Директ, VK, Telegram, исследованиями и PDF. В переносимый архив вошли агрегированные показатели личной проверки. Содержимое старых вложений отдельно не анализировалось.')
    comp+=par(f'Установленные адаптеры дали {reg["work"]["tuned"]}/{reg["work"]["total"]} на прежних рабочих тезисах, {reg["routing"]["tuned"]}/{reg["routing"]["total"]} в выборе сочетаний инструментов и {reg["RCB"]["tuned"]}/{reg["RCB"]["total"]} на отдельной прежней публичной проверке. При свободной генерации {m["generation"]["identical"]} из {m["generation"]["questions"]} выходных последовательностей совпали с исходной Qwen по каждому токену. Для каждого запроса приложение явно задаёт масштабы всех загруженных адаптеров.')
    names={'code':'Генерация, разбор и исправление Python','constrained_json':'JSON по схеме','constraints':'Следование ограничениям','context':'Извлечение из длинного входа','function_calling':'Имя и аргументы инструмента','json':'Свободный JSON','languages':'Русский и английский','logic':'Логика','math':'Короткая математика','stability':'Повтор при фиксированных параметрах','uncertainty':'Неоднозначные запросы','untrusted_data':'Цитаты и недоверенный текст'}
    comp+=table(['Диагностика самой модели','Без адаптеров','3.2.1','3.3'],[[title]+[str(m['diagnostics'][v][name][0])+'/'+str(m['diagnostics'][v][name][1]) for v in ('raw','current','final')] for name,title in names.items()])
    comp+=par('Для кода применены исполняемые тесты чистых функций и ограниченный синтаксис. JSON сравнивался после разбора с учётом типов, имена и аргументы вызовов инструментов проверялись отдельно. Функции в этом экзамене не выполнялись. Сценарии с цитатами моделируют безвредные противоречивые указания в записи и не исчерпывают безопасность компьютерного агента.')
    comp+=heading('Сколько ждёт человек в окне чата')
    times=[]
    for k,title in [('all','Все 23 контрольных запроса'),('numeric','Формулы'),('history','Отсылка к прежней реплике'),('json','JSON'),('quote','Запрошенное поле'),('mixed','Текстовая задача с выражением')]:
        samples=[[r['latency'] for r in rows if not r['kind'].endswith('dev') and (k=='all' or r['kind']==k)] for rows in apps]
        medians=[median(a) for a in samples]
        times.append([title,str(len(samples[0])),fmt(medians[0],3),fmt(medians[1],3),fmt(quantiles(samples[0],n=100,method='inclusive')[94],3),fmt(quantiles(samples[1],n=100,method='inclusive')[94],3)])
    comp+=table(['Запрос','Число','До · медиана, с','После · медиана, с','До · p95, с','После · p95, с'],times)
    comp+=par('Время отсчитано от отправки до завершения ответа через штатный обработчик WPF. Здесь учитываются маршрутизация, работа агентов и вывод текста. Разброс в коротких сериях велик, особенно если один маршрут ошибочно запускал генератор изображения.')
    comp+=heading('Скорость, очередь и ресурсы движка')
    perf_table=[]
    for label,data in zip(('3.2.1','3.3','Q8 · эксперимент'),perf):
        for clients,row in data.items():
            res=row['resources']
            perf_table.append([label,clients,fmt(row['ttft']['p50'],2)+' / '+fmt(row['ttft']['p95'],2),
                               fmt(row['latency']['p50'],2)+' / '+fmt(row['latency']['p95'],2),
                               fmt(row['tps']['p50'],2),fmt(row['aggregate_tps'],2),fmt(res['peak_server_working_mib'],0)+' / '+fmt(res['peak_gpu_board_mib'],0),fmt(res['gpu_board_wh'],3),str(row['errors'])+'/12'])
    comp+=table(['Вариант','Клиентов','TTFT p50 / p95, с','Ответ p50 / p95, с','Генерация p50, ток/с','Суммарно, ток/с','RAM процесса / VRAM платы, МиБ','Вт·ч / 12','Ошибки'],perf_table)
    comp+=par('Для каждой нагрузки запускались 12 одинаковых по версии запросов по 32 выходных токена после прогрева. С одним клиентом выделен один слот, с двумя и четырьмя — два слота. Очередь входит в пользовательскую задержку. RAM — рабочий набор процесса, VRAM и энергия относятся ко всей видеоплате. Снимки nvidia-smi делались каждые 500 мс. На p95 при двенадцати наблюдениях и на сравнение соседних процентов влияют фоновые процессы и температура ноутбука.')
    comp+=table(['Один клиент · те же запросы','3.2.1','3.3','Разница','Относительно исходного'],[
        metric_row('Медианный первый токен, с',perf[0]['1']['ttft']['p50'],perf[1]['1']['ttft']['p50'],3),
        metric_row('p95 ответа, с',perf[0]['1']['latency']['p95'],perf[1]['1']['latency']['p95'],3),
        metric_row('Суммарные токены/с',perf[0]['1']['aggregate_tps'],perf[1]['1']['aggregate_tps']),
        metric_row('Энергия видеоплаты, Вт·ч / 12 запросов',perf[0]['1']['resources']['gpu_board_wh'],perf[1]['1']['resources']['gpu_board_wh'],3),
        metric_row('Загрузка движка, с',read(base/'current/results.json')['startup_s'],read(base/'final/results.json')['startup_s'],2)])
    technical=heading('Веса и данные')+par(f'Обучение общего проверяющего меняет ранговое дополнение 3 к выходной проекции Qwen. Признаки сняты с последнего слоя на исходной Q4_K_M. Три токена начала вердикта обучены взвешенной регуляризованной регрессией. Форма A — 3 × 4 096, B — 248 320 × 3. Выбранные на разработке параметры ridge {fmt(alpha["ridge"])}, целевой отступ {fmt(alpha["gap"])}, множитель {fmt(alpha["strength"])}. В файле GGUF две матрицы FP16, общий размер 1 514 880 байт. Хеш выбранного файла закреплён до закрытого экзамена.')
    technical+=table(['Артефакт','Размер или выборка','SHA-256'],[
        ['Базовый GGUF','5 680 522 464 байта',origin['zarum-9b-q4_k_m.gguf']['sha256']],
        ['Выбранный общий адаптер','1 514 880 байт',ev['development']['weight_sha256']],
        ['Шаблон GGUF',str(arch['tokenizer.chat_template']['characters'])+' символов',arch['tokenizer.chat_template']['sha256']],
        ['Исходный архив RCB','339 обучение / 48 разработка / 83 закрытая проверка',origin['RCB.zip']['sha256']],
        ['Личный закрытый набор','30 разных бесед',privacy['sha256']],
        ['MSI 3.2.1 для отката','45 158 400 байт',origin['Sfera-3.2.1.msi']['sha256']]])
    technical+=par('Производственный путь включает отдельный старый адаптер для делового текста, персональный адаптер рабочего формата, маршрутизацию, локальные файлы и явно разрешаемый веб-поиск. Две роли могут выполнять независимые запросы параллельно. Выбор проверяющего зависит от материала. На обычном ответе адаптеры вердикта выключены.')
    technical+=par('У второго кандидата та же форма матриц. По RCB подбирались размер проекции признаков и сила регуляризации. Грамматика ограничивает начало ответа тремя вердиктами. Контрольный SHA-256 второго файла '+escape(transfer['candidate']['weight_sha256'])+'. Веса первого и второго опытов хранятся раздельно.')
    technical+=table(['Файл проверенной сборки 3.3','Размер, байт','SHA-256'],[[key,str(data['bytes']),data['sha256']] for key,data in m['release_files'].items()])
    technical+=heading('Повторение на этом ПК')+par('В архиве есть публичные исходники приложения и тестов, закреплённые ревизии, типы тензоров, исходный шаблон, манифесты выборок и обезличенные измерения. Из корня проекта в PowerShell можно повторить одну серию в новой папке. Для тестов сети нужны Windows, Python 3.12 с NumPy, установленная «Сфера» с Qwen 9B и модуль CUDA. Данные загружаются один раз и проверяются по SHA-256. Выборка IANA включена для повторения той же ревизии.')
    technical+='<pre>./research/reproduce.ps1 -Study App\n./research/reproduce.ps1 -Study Weights\n./research/reproduce.ps1 -Study Network\n./research/reproduce.ps1 -Study Mathematics</pre>'
    technical+=par('Вариант Private читает закрытую разметку из профиля Windows пользователя. Её содержимое и персональные веса исключены из переносимого архива. Пакет для повторения содержит только агрегат результата личной проверки. Параметр BeforeBuild принимает папку прежней версии. Полный набор запускается последовательно, чтобы одна видеокарта не обслуживала два конкурирующих эксперимента.')
    technical+=par(link('Материалы и исходный код','Материалы.zip')+' · '+link('Численные результаты','Результаты.json')+' · '+link('Журнал экспериментов','Журнал экспериментов.json'))
    deploy=par('На этот ПК установлен релиз '+escape(installed['version'])+'. После установки проверены версия EXE, совпадение с собранным файлом, файлы адаптеров, четыре официальные библиотеки CUDA, ярлык и ответ главного окна. Содержимое всех сохранённых до установки чатов проверено по SHA-256 ('+str(installed['chats_unchanged'])+'/'+str(installed['chat_count'])+'). Сохранены персональный профиль, настройки и действующий приватный адаптер.')
    deploy+=par('Для установки откройте Sfera-Install.exe. На другом компьютере без интернета оставьте рядом папки Models и Acceleration, чтобы сразу получить 9B и ускорение. Один EXE самодостаточен для компактного профиля Qwen 4B с анализом изображений, SDXS и Whisper. В меню «Модели и настройки» показан выбранный профиль.')
    live=m['installed_app'];ireg=m['installed_registry']
    deploy+=par('После установки проверки повторены с файлами из Programs\\SferaAI. В окне чата получено '+str(live['history'][0])+'/5 по давним репликам, '+str(live['numeric'][0])+'/6 по выражениям и '+str(live['json'][0])+'/4 по JSON. Повторный результат на трёх группах IANA — '+', '.join(str(x[0])+'/'+str(x[1]) for x in ireg)+'. В отдельной пустой папке установщик извлёк и проверил по SHA-256 четыре встроенных файла суммарным объёмом '+str(m['standalone']['verified_bytes'])+' байт.')
    ui=m['studio']
    deploy+=par('В контрольной задаче о двух типографиях аналитик и проверяющий работали одновременно '+fmt(ui['parallel_seconds'],2)+' с. В карточках ролей сохранено '+str(ui['unique_frames'])+' различных кадров анимации из '+str(ui['frames'])+'. Проверены автоматический запуск рисования, обычный ответ, чтение изображения, снимок экрана и размещение тестового окна Windows. При первом запуске этой проверки Windows отказала в замене файла настроек старого тестового профиля. Повтор в новой изолированной папке прошёл полностью. Отдельная проверка выполнила '+str(ui['save_operations'])+' последовательных сохранений с обратным чтением без ошибки.')
    deploy+=par('В установленной программе локальный голос '+escape(m['voice']['speaker'])+' произнёс фразу «Подготовь презентацию для встречи с клиентом». Локальный Whisper вернул «'+escape(m['voice']['recognized'].strip().rstrip('.'))+'». Для проверки использована синтезированная аудиозапись.')
    deploy+=par('После обновления можно открыть знакомый чат и проверить вопрос по прежней реплике, деловой тезис и свою типичную задачу. Журнал локального движка находится в %LOCALAPPDATA%\\SferaAI\\logs\\chat.log. Для сравнения скорости используйте те же условия питания, список запросов и число клиентов. Обычный текст не отправляется на сервер модели в интернете. Разрешённый веб-поиск обращается к внешним сайтам, диктовка и генерация изображений работают локально.')
    deploy+=par('Для полного отката закройте «Сферу», удалите текущую версию через «Установленные приложения» Windows и откройте сохранённый MSI 3.2.1. Модели и чаты лежат отдельно в %LOCALAPPDATA%\\SferaAI. Перед откатом можно скопировать эту папку. Полный цикл удаления на активном пользовательском профиле не проводился. Предыдущий бинарный файл запускался в изолированном профиле при сравнении. '+link('Сохранённый установщик 3.2.1','Сфера-3.2.1.msi'))
    nexts=par('Приоритет для следующей разметки — реальные пары «фрагмент и тезис» из работы, для которых человек различает противоречие и отсутствие сведений. В закрытом RCB встречаются всего два противоречия. Для улучшения логики, английского и самостоятельного написания кода нужен отдельный, более широкий экзамен с проверяемыми ответами. Повторный подбор на 83 опубликованных ответах не заменит новую закрытую выборку.')
    nexts+=par('Поиск давних реплик сейчас опирается на совпадающие слова и имена внутри текущего чата. Пять успешных примеров с явными ссылками проверяют только такой вид запроса. Чтобы оценить поиск по смыслу, нужны новые пары вопросов с перефразировками, несколькими похожими проектами и исправлениями сроков. Для проверки микрофона конкретного пользователя всё ещё требуется живая диктовка в его акустической обстановке.')
    css='''*{box-sizing:border-box}html{scroll-behavior:smooth}body{margin:0;color:#202837;background:#f7f7f4;font:16px/1.62 "Segoe UI",sans-serif}main{max-width:1090px;margin:auto;padding:48px clamp(18px,5vw,62px) 88px;background:white}h1{font-size:clamp(36px,6vw,64px);line-height:1.06;letter-spacing:-.05em;margin:8px 0 18px}h2{margin:0 0 20px;font-size:30px;line-height:1.2;letter-spacing:-.035em}h2 span{color:#6b52a5;font-size:.72em;margin-right:8px}h3{margin:26px 0 8px;font-size:19px}p{max-width:86ch;margin:12px 0}section{padding:34px 0 24px;border-top:1px solid #dcdfe4}a{color:#533e91;text-underline-offset:3px}nav{display:flex;flex-wrap:wrap;gap:8px 20px;margin:24px 0 33px}nav a{font-size:14px}.scroll{overflow-x:auto;margin:14px 0 20px}table{border-collapse:collapse;min-width:600px;width:100%;font-size:14px;line-height:1.47}td,th{text-align:left;vertical-align:top;border-bottom:1px solid #d9dce1;padding:9px 10px}th{background:#eeeaf6;color:#3b2d58}td:first-child{font-weight:600}td{overflow-wrap:anywhere}tbody tr:nth-child(even){background:#f9fafb}pre{overflow:auto;padding:16px 18px;background:#f1eff6;border-radius:5px;font-size:13px}.intro{color:#504a5a;font-size:18px}.kicker{font-size:13px;color:#645184;font-weight:650;letter-spacing:.04em}@media print{main{padding:0;max-width:none}nav{display:none}section{break-before:auto}tr{break-inside:avoid}.scroll{overflow:visible}table{min-width:0;font-size:10px}h2,h3{break-after:avoid}}'''
    summary=par('В рабочем чате «Сфера» нашла '+str(after['history'][0])+' из 5 давних договорённостей, вернула '+str(after['json'][0])+' из 4 точных JSON-объектов и сократила медиану ответа на формулу с '+fmt(calc[0],2)+' до '+fmt(calc[1],3)+' с. '+('Новый проверяющий по источнику установлен.' if decision['general_evidence'] else 'Два новых набора весов для проверки утверждений остались экспериментальными. На компьютере работают прежние проверенные специализации Qwen.'))
    page='<!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Исследование Сферы 3.3</title><style>'+css+'</style><main><div class="kicker">20 ноября 2026 · Windows · Qwen3.5-9B</div><h1>Исследование «Сферы» 3.3</h1><div class="intro">'+summary+'</div>'+plain
    parts=[('A','Аудит модели',audit),('B','Журнал экспериментов',journal),('C','Результаты до и после',comp),('D','Веса, конфигурация и код',technical),('E','Установка, мониторинг и откат',deploy),('F','Следующие проверки',nexts)]
    page+='<nav aria-label="Содержание">'+''.join(link(t,'#'+i) for i,t,_ in parts)+'</nav>'+''.join(section(*part) for part in parts)+'</main></html>'
    (dist/'Отчёт.html').write_text(page,encoding='utf8')


def package(base,dist):
    release=read(base/'release.json')
    inputs=[ROOT/'build.ps1']
    for name in ('src','packaging','training','research'):
        inputs.extend(p for p in (ROOT/name).rglob('*') if p.is_file() and '__pycache__' not in p.parts and p.suffix not in ('.pyc','.nupkg'))
    with zipfile.ZipFile(base/'baseline/source-3.2.1.zip') as prior:
        originals={name:hashlib.sha256(prior.read(name)).hexdigest() for name in prior.namelist()}
    source_hashes={p.relative_to(ROOT).as_posix():sha(p) for p in inputs}
    changes=[dict(file=k,before=originals.get(k),after=source_hashes.get(k)) for k in sorted(set(source_hashes)|set(originals)) if originals.get(k)!=source_hashes.get(k)]
    put(base/'source-changes.json',changes)
    public={'baseline/gguf.json','baseline/chat-template.jinja','baseline/baseline.json','baseline/source-3.2.1.zip','baseline-complete.json',
            'sources/sources.json','sources/qwen-config.json','sources/qwen-license.txt','sources/iana-http-status.csv','registry/manifest.json',
            'math/manifest.json','math/baseline.json','math/develop.json','evidence/manifest.json','evidence/baseline-dev.json','evidence/baseline-closed.json','evidence/final-closed.json',
            'evidence/candidate.json','current/results.json','raw/results.json','final/results.json','kv8/results.json','guard.json',
            'app-before/application.json','app-final/application.json','registry-before/application.json','registry-final/application.json',
            'free/free-generation.json','legacy-regressions/results.json','release.json','kv8-matched/results.json','app-installed/application.json','registry-installed/application.json',
            'transfer/manifest.json','transfer/candidate.json','transfer/raw-external.json','transfer/grammar-external.json','transfer/candidate-external.json','transfer/candidate-regression.json','deployed-evidence.json','source-changes.json','voice-installed/result.json','ui-observations.json','studio-check.txt'}
    with zipfile.ZipFile(dist/'Материалы.zip','w',zipfile.ZIP_DEFLATED,compresslevel=5) as z:
        for path in sorted(set(inputs)):z.write(path,path.relative_to(ROOT))
        for name in sorted(public):z.write(base/name,'.build/research321/'+name)
        z.write(base/'sources/iana-http-status.csv','registry/iana-http-status.csv')
        for name in ('Результаты.json','Журнал экспериментов.json'):z.write(dist/name,name)
        z.write(base/'evidence'/PUBLIC_WEIGHT,'experimental/rcb-first.gguf')
        if not release['general_evidence']:z.write(base/'transfer'/PUBLIC_WEIGHT,'experimental/rcb-classifier.gguf')
    rollback=dist/'Сфера-3.2.1.msi'
    if not rollback.exists():os.link(base/'baseline/Sfera-3.2.1.msi',rollback)
    if sha(rollback)!=read(base/'baseline/baseline.json')['files']['Sfera-3.2.1.msi']['sha256']:raise ValueError('Rollback hash changed')
    with zipfile.ZipFile(dist/'Материалы.zip') as z:
        if z.testzip():raise ValueError('Invalid research archive')
    manifest={p.name:dict(bytes=p.stat().st_size,sha256=sha(p)) for p in dist.iterdir() if p.is_file() and p.name!='SHA256.json'}
    put(dist/'SHA256.json',manifest)


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--run',type=Path,default=ROOT/'.build/research321')
    parser.add_argument('--out',type=Path,default=ROOT/'dist/Исследование Сферы')
    args=parser.parse_args();compile_report(args.run,args.out);package(args.run,args.out)
