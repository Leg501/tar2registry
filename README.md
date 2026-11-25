# tar2registry

Простой веб-интерфейс к приватному Docker-реестру (Registry v2): **витрина образов + загрузка tar-архивов кнопкой из браузера**. Заточен под сценарий «офлайн-комплект»: образы собираются где-то на стороне, приезжают на сервер в виде tar (`docker save`), и их нужно положить в локальный реестр.

A simple web UI for a private Docker registry (Registry v2): **a browsable catalog of images plus tar archive uploads straight from the browser**. Built for the "offline image kit" workflow: images are built elsewhere, arrive at the server as tars (`docker save`), and need to land in the local registry.

---

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

---

# English

A lightweight web UI for a private Docker registry (Docker Registry API v2): **an image catalog plus one-button uploads of tar archives from the browser**. Designed for the "offline image kit" workflow: images are built elsewhere, shipped to the server as tars produced by `docker save`, and need to be pushed into the local registry.

## Features

- **Catalog**: repositories and tags with a live filter (by repo or tag name) and a "copy docker pull" button.
- **Tar upload**: pick a file — the server reads `manifest.json` inside the archive and suggests the repo path and tag; the image is pushed via `skopeo copy docker-archive:` (no Docker daemon required).
- **Optional password gate** for the UI; registry credentials are supplied only via environment variables.
- **Tag deletion** behind a flag, disabled by default.
- One small container: .NET 8 + skopeo. UI is in Russian.

## Environment variables

| Variable | Meaning | Default |
|---|---|---|
| `REGISTRY_URL` | registry address | `http://registry:5000` |
| `REGISTRY_USER` / `REGISTRY_PASSWORD` | registry credentials (basic auth) | empty |
| `APP_PASSWORD` | UI login password; empty = no login | empty |
| `ENABLE_DELETE` | `true` to allow deleting tags from the UI | `false` |
| `MAX_UPLOAD_GB` | upload size limit | `5` |

## Running

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

If the registry runs on the host rather than as a container, use `network_mode: host` and `REGISTRY_URL: http://localhost:5000`.

## Building the image

A push to `main` triggers GitHub Actions: the image is built and published to `ghcr.io/<owner>/tar2registry` (tags `latest` + commit sha). Locally:

```bash
docker build -t tar2registry .
```

## Limitations

- The tar must be the output of `docker save` (must contain `manifest.json`); OCI archives (`skopeo copy ... oci-archive:`) are not accepted for upload.
- Registry over plain HTTP: `--dest-tls-verify=false`, intended for an internal network.
- Deleting a tag marks the manifest as deleted; disk space is reclaimed only after the registry's garbage collection.
