# tar2registry

Простой веб-интерфейс к приватному Docker-реестру (Registry v2): **витрина образов + загрузка tar-архивов кнопкой из браузера**. Заточен под сценарий «офлайн-комплект»: образы собираются где-то на стороне, приезжают на сервер в виде tar (`docker save`), и их нужно положить в локальный реестр.

A simple web UI for a private Docker registry: browse repositories/tags and upload `docker save` tar archives from the browser. Built for offline/image-kit workflows.

## Возможности

- **Витрина**: список репозиториев и меток, живой фильтр по имени/метке, кнопка «copy pull».
- **Загрузка tar**: выбираете файл — сервер читает `manifest.json` из архива и подсказывает имя и метку; грузит в реестр через `skopeo copy docker-archive:` (Docker-демон не нужен).
- **Пароль на вход** (опционально), кредиты реестра только через переменные окружения.
- **Удаление меток** — по флагу, по умолчанию выключено.
- Всё на русском, один маленький контейнер (.NET 8 + skopeo).

## Переменные окружения

| Переменная | Значение | По умолчанию |
|---|---|---|
| `REGISTRY_URL` | адрес реестра | `http://registry:5000` |
| `REGISTRY_USER` / `REGISTRY_PASSWORD` | креды реестра (basic auth) | пусто |
| `APP_PASSWORD` | пароль входа в UI; пусто = без входа | пусто |
| `ENABLE_DELETE` | `true` — разрешить удаление меток в UI | `false` |
| `MAX_UPLOAD_GB` | лимит размера tar | `5` |

## Запуск

```yaml
services:
  tar2registry:
    image: ghcr.io/OWNER/tar2registry:latest
    ports: ["5001:8000"]
    environment:
      REGISTRY_URL: http://registry:5000
      REGISTRY_USER: deploy
      REGISTRY_PASSWORD: ...
      APP_PASSWORD: ...
```

Если реестр работает на хосте, а не контейнером — используйте `network_mode: host` и `REGISTRY_URL: http://localhost:5000`.

## Сборка образа

Пуш в `main` запускает GitHub Actions: образ собирается и публикуется в `ghcr.io/<владелец>/tar2registry` (метки `latest` + sha). Локально:

```bash
docker build -t tar2registry .
```

## Ограничения

- tar должен быть продуктом `docker save` (содержать `manifest.json`); OCI-архивы (`skopeo copy ... oci-archive:`) для загрузки не годятся.
- Реестр без TLS: `--dest-tls-verify=false`, предполагается внутренняя сеть.
- Удаление метки помечает манифест удалённым; до сборки мусора в registry место не освободится.
