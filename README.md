# CivicReport MVP

## Visão geral
MVP com API .NET 8, worker de moderação, dashboard web e infraestrutura via Docker Compose.

### Stack
- **Backend**: .NET 8 Minimal API
- **DB**: PostgreSQL + PostGIS
- **ORM**: EF Core
- **Auth**: JWT
- **OTP**: provider mockado com hash e expiração
- **Upload**: S3 presigned URL (MinIO no dev)
- **Worker**: Background Service
- **Dashboard**: React + Vite + Leaflet

## Como rodar

```bash
cd infra
docker compose up --build
```

A API sobe em `http://localhost:5000`, o dashboard em `http://localhost:5173`, o MinIO em `http://localhost:9000` e o console do MinIO em `http://localhost:9001`.

### Migrations
A API aplica automaticamente as migrations na inicialização (chama `Database.Migrate()`).

### Seed dev
Em `ASPNETCORE_ENVIRONMENT=Development` a API cria:
- Admin: `+5511990000000`
- Usuário comum: `+5511990000001`

## Fluxo de teste rápido
1) Solicite OTP
```bash
curl -X POST http://localhost:5000/auth/request-otp \
  -H "Content-Type: application/json" \
  -d '{"phone": "+5511990000000"}'
```
Em Development a resposta inclui `otp_code`.

2) Verifique OTP e capture o token
```bash
curl -X POST http://localhost:5000/auth/verify-otp \
  -H "Content-Type: application/json" \
  -d '{"phone": "+5511990000000", "otp": "123456"}'
```

3) Solicite URL de upload
```bash
curl -X POST http://localhost:5000/reports/request-upload \
  -H "Authorization: Bearer <TOKEN>" \
  -H "Content-Type: application/json" \
  -d '{"contentType":"image/jpeg"}'
```

4) Faça upload usando o `uploadUrl` (PUT) e crie o report
```bash
curl -X POST http://localhost:5000/reports \
  -H "Authorization: Bearer <TOKEN>" \
  -H "Content-Type: application/json" \
  -d '{"category":"Dengue","description":"Foco de dengue","lat":-22.7,"lng":-47.6,"accuracyMeters":15,"fileKey":"<FILE_KEY>"}'
```

5) O worker aprova automaticamente (se não cair em revisão) e o report aparece no feed público:
```bash
curl http://localhost:5000/feed
```

## Endpoints principais
- `POST /auth/request-otp`
- `POST /auth/verify-otp`
- `POST /reports/request-upload`
- `POST /reports`
- `GET /feed`
- `GET /reports/{id}`

Admin:
- `GET /admin/reports`
- `GET /admin/reports/review?status=NEEDS_REVIEW|PENDING_MODERATION`
- `POST /admin/reports/{id}/approve`
- `POST /admin/reports/{id}/reject`
- `GET /admin/reports/export.csv`

## Coleção de requests
Veja `requests.http` para exemplos.
