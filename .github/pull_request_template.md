## Що змінено

<!-- Коротко: яку задачу розв'язує ця зміна. / Briefly: what problem this solves. -->

## Докази

Заповніть те, що стосується зміни. Порожній розділ — це не «не потрібно», а «не перевірено».
Fill in what applies. An empty section means "not verified", not "not needed".

- [ ] `powershell -File .\test.ps1` — зелений (вкажіть числа / paste the numbers):
  ```
  RESULT: __ PASS, 0 FAIL
  PROOF GATE: __ PASS / 0 FAIL
  DRIVE RESULT: __ PASS, 0 FAIL
  ```
- [ ] `.\.standard\conform.ps1 -Root .` — 0 FAIL
- [ ] `.\tools\mutate.ps1` — 100 % CAUGHT (потрібно, якщо додано нову гарантію /
      required if a new guarantee was added)
- [ ] **Видима зміна:** знімки з `docs/img/` до і після, звірені **оком** /
      **Visible change:** before/after shots from `docs/img/`, checked by **eye**
- [ ] Кожен новий видимий рядок — через `L.S("укр", "eng")`

## Чого ця зміна НЕ покриває

<!-- Чесний перелік меж. Найкорисніший розділ у цьому шаблоні. -->
<!-- An honest list of limits. The most useful section in this template. -->
