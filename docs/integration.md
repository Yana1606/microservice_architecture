# Контракт внешних сервисов

Конверт Event v1 описан в OpenAPI и Contracts.cs. Producer должен соответствовать отдельному bearer-ключу. ID — UUID без дефисов, неизменяемый при повторной публикации. CorrelationId=tournamentId, CausationId указывает вызвавшее событие, OccurredAt — UTC для аудита, не средство упорядочения. Неизвестная версия даёт unsupported_event. Новую несовместимую схему следует публиковать с новой версией и обработчиком; сейчас поддерживается v1.

| Отправитель | Событие / payload | Получатели |
|---|---|---|
| tournament | TournamentStarted / полный Tournament после закрытия регистрации | team, checking (в демо simulator), scores, results |
| tournament | GameStarted / Game с конфигурацией и абсолютными UTC-сроками | team/checking (simulator), scores, results |
| tournament | SolutionsClosed, AppealsClosed / соответствующий UTC-срок | team/checking (simulator), scores, results |
| team | SolutionBound / Binding(anonymousId,teamId,taskId,formType) | **только scores**, не checking |
| team | TeamSealed / полный список anonymousId принятых решений игры | scores |
| checking | VerdictRecorded / Verdict(anonymousId,revision,outcome,juryId,authority) | scores |
| checking | ChecksSealed / полный список event ID финальных вердиктов игры, включая первичные и апелляционные | scores |
| scores | ScoresReady / ScoreSnapshot(rows,appliedVerdicts) | results |
| results | GameFinalized / список Standing(teamId,score,place,points) | tournament, scores, team/checking (simulator) |
| results | WinnerDetermined / список Standing всех команд; score=сумма очков турнира | tournament, scores, simulator |

Прочие события жизненного цикла и изменения баллов перечислены в architecture.md. Для симуляции они сохраняются в отдельной БД simulator.

## Приватность

AnonymousReview передаётся team → checking: anonymousId, taskId, photoUrl, statement, reference, assignedJury, appeal, reason. QR содержит только случайный номер бланка. Team хранит владельца; checking не имеет доступа к Binding, общей шине с командами или онлайн-местам. Фото надо очищать от идентификаторов и метаданных до назначения. Фото/условия/эталоны не публикуются в таблице или публичном REST. Организаторский GET конфигурации защищён.

## Обязанности team

TournamentStarted → подготовить бланки всех игр; хранить частное соответствие владельца. GameStarted + готовые бланки + корректные стартовые задачи + запрос организатора → выдача. Приём фото проверяет UTC-срок, владение, выдачу, однократность сдачи, QR и наличие фото **в одной транзакции**. Причины отказов: solutions_closed, form_not_owned, form_not_issued, form_already_submitted, invalid_qr, photo_required. Отклонённые фото не поступают checking. Успешная сдача атомарно публикует приватный SolutionBound в scores и AnonymousReview в checking. Выдача следующего задания зависит от настроенных правил конкретного типа игры; три реализованных сервиса её не решают. Вопрос допускается только во время игры по выданному заданию; ответ связан с существующим вопросом и доступен его автору. Вопрос проверяющему анонимен.

## Обязанности checking

Решение ожидает свободного жюри, назначается ровно одному; принятие вердикта требует совпадения назначенного juryId. Исход первичной проверки — correct / incorrect / refuse. Refuse не является неверным решением и не начисляет баллы: обязательно назначить senior, дождаться correct/incorrect, затем выдать один VerdictRecorded revision=1, Authority=Senior. Обычное решение жюри имеет Authority=Jury. Эскалация и назначение — внутренние события checking (ReviewAssigned, ReviewRefused, ReviewEscalated); scores получает только окончательное решение этапа.

Апелляция требует Incorrect, непустое обоснование, now<AppealsDue и отсутствия прошлого обращения `(tournament,team,task)`. Это ограничение хранится на весь турнир, включая разные игры и закрытые окна. Причины отказа: task_not_incorrect, appeal_deadline_expired, appeal_already_used, reason_required, not_team_captain. Одна принятая апелляция создаёт одну анонимную перепроверку другому члену жюри; ему выдаётся пометка appeal=true, фото, условие, эталон, обоснование. Refuse обязательно эскалирует senior; решение требует действительного назначения. Итог revision=2 имеет outcome=AppealUpheld/AppealRejected; повторное обращение не создаёт revision=3. Конфликтующие финальные вердикты — ошибка интеграции, не новая поправка.

ChecksSealed создаётся только после закрытия обоих приёмов, получения полного TeamSealed, завершения каждой первичной проверки и каждой поданной апелляции (включая эскалированные). Включает IDs всех опубликованных VerdictRecorded. После seal запрещено создавать новые принятые решения/апелляции/вердикты. Решение о закрытии нельзя основывать только на текущем пустом списке проверок: необходимо знать полный манифест принятых решений team.

Имитатор `POST /fixtures/{tournament}/{game}`, bearer simulator-demo: создаёт сохраняемый набор Binding/вердиктов/двух seal, отправляет в обратном порядке, повторяет те же IDs при повторном вызове. Это исключительно тестовый API. Он не предоставляет интерфейс судьи и не имитирует проверку прав, сканирование QR или реальную подачу апелляции.
