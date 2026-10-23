---
name: projectstudio-mcp-electrica-lighting
description: Расчёт Ки и размещение освещения в Электрике; логическое подключение выключателей.
---

# Освещение

Помещения уже должны существовать. Их границы на подоснове определяет пользователь; собственные простые контуры можно создать contour_create. RoomHeightMm — высота комнаты; RoomFloorHeight — высота будущих комнат на этаже. Этажу задаются FloorHeightMm и RoomFloorHeight; порядок JSON не важен.

Для Ки выбери DbElLighting через database_search и импортируй в БД проекта. electrica_lighting_place принимает items с planPath, roomId, dbId и параметрами: lux (по умолчанию300), mountingHeight (3000мм), workSurfaceHeight (800мм), ceilingReflectance/wallsReflectance/floorReflectance, reserveFactor. По руководству отражение0.7/0.5/0.3 и запас1.3; это параметры примера, не универсальные нормативы. Монтаж не выше RoomHeightMm, рабочая поверхность ниже монтажа.

Команда выполняет настоящий расчёт и штатную расстановку; ответ — число/ID новых светильников и освещённость. Те же параметры нескольких комнат передаются элементами items. Если комната уже населена, replace=true явно разрешает удалить/заменить её светильники. Для комнаты без прямоугольных расчётных областей команда сообщит причину: попроси пользователя разбить её штатным инструментом или согласуй пропуск.

Без расчёта светильники создаются objects_create с defaults (type=ElLighting, dbType/dbId, properties.MountingHeightMm), items задают позиции. Это размещение по заданию, не замена расчёта. ugo_search позволяет выбрать ugoName/ugoFile, также для Ки.

Выключатель: найди тип/изделие, создай objects_create; параметры и высоту узнай через types_describe. objects_structure(relation=ports) показывает порты клавиш. ports_connect назначает светильники порту, а клавиши — группе щита. Проверка — objects_structure(relation=slaves). Геометрические трассы и логическая принадлежность независимы; прокладка описана в electrica-cables.
