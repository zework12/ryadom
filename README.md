# Рядом

Удалённая помощь на Windows: один человек показывает свой экран, другой видит его и управляет мышью и клавиатурой. Одно приложение на обе стороны — «Мне нужна помощь» и «Я помогаю».

- Видео до 90 кадров/с (H.264, WebRTC через свой сервер [LiveKit](https://livekit.io)).
- Подключение по 9-значному коду; экран и управление включаются только после «Разрешить» на той стороне. Пока идёт помощь, у человека рамка по краю экрана и кнопка «Отключить».
- Курсор той же формы и без задержки, общий буфер обмена, Caps/Num Lock, выбор монитора, кадров и качества.
- Установщик, удаление, тихое обновление.
- Вход для программ-помощников на стороне «Я помогаю» (только `127.0.0.1`, с ключом): картинка, клики, клавиши, [agent-desktop](https://github.com/vercel-labs/agent-desktop) на той стороне и фоновые задачи — см. `tools/agent.sh`.

## Устройство

| Папка | Что там |
|-------|---------|
| `src/Ryadom` | приложение (WPF, .NET 10): `Host/` — сторона «Мне нужна помощь», `Viewer/` — «Я помогаю», `Live/` — клиент к `livekit_ffi.dll`, `Views/` и `Theme/` — окна |
| `src/Ryadom.Proto` | сообщения livekit-ffi (protobuf, Apache-2.0, из [livekit/rust-sdks](https://github.com/livekit/rust-sdks)) |
| `server/` | выдача кодов и пропусков LiveKit (Python, только стандартная библиотека) |
| `design/` | отдельная сборка окон для превью в PNG |
| `site/` | страница скачивания |

## Сборка

Нужны .NET SDK 10 и две готовые библиотеки рядом с проектом:

- `../tools/livekit-ffi/livekit_ffi.dll` — из релизов [livekit/rust-sdks](https://github.com/livekit/rust-sdks/releases) (`ffi-v0.12.x`, windows-x86_64);
- `tools/agent-desktop/agent-desktop-win32-x64.exe` — из npm-пакета `agent-desktop` 1.3.1 (необязательно: без него команды `ad` недоступны).

```sh
sh build-check.sh      # отладочная сборка
sh publish.sh          # один Ryadom.exe в dist/
```

Адрес сервера задан в `src/Ryadom/Net/Api.cs`; свой — `Ryadom.exe --server https://host/ryadom`.

## Лицензия

MIT. Сторонние части — под своими лицензиями (LiveKit — Apache-2.0, agent-desktop — Apache-2.0).
