# BuildAI ACC Issue Return

Отдельное дополнение и отдельный MSI для возврата ACC Issues в Revit. Основной BuildAI, его Ribbon и основной MSI не изменяются.

Старый штатный механизм маркера сохранён: `DirectShape` в `OST_GenericModel`, две экструдированные коробки, красные overrides. В новом дополнении маркер хранится по ключу hub/project/container/Issue и обновляется через Extensible Storage.

Координаты рассчитываются для каждого Issue отдельно: единицы → его globalOffset → его placement/ref-point transform → единицы Revit → transform соответствующего RevitLinkInstance → host. Выполняется обратное преобразование; ошибка больше 10 мм блокирует импорт.
