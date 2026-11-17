# StatusHub

SaaS de surveillance de disponibilité avec pages de statut publiques.

## Structure

- `backend/` : solution .NET 10 (`StatusHub.sln`) : Domain, Application, Infrastructure, Api, 4 Lambdas (`Functions.*`), `LocalHost` (worker local), `tests/`.
- `frontend/` : Angular 20 (standalone, signals, Vitest).
- `docker-compose.yml` : DynamoDB Local, ElasticMQ (SQS) et MailHog (emails), sans AWS.
- `docs/` : documentation.

## Lancer en local

```bash
docker compose up -d                       # DynamoDB Local :8000, ElasticMQ :9324, MailHog UI :8026
cd backend
dotnet run --project src/StatusHub.Api     # API + worker local (Scheduler -> Checker -> Evaluator -> Notifier)
cd ../frontend
npm install
npm start                                  # http://localhost:4200
```

Le worker local est activé par `StatusHub:LocalWorker=true` (défaut en `Development`). Il utilise les mêmes
classes (`IStage<T>`) que les Lambdas ; seul le point d'entrée change.

## Build et tests

```bash
cd backend  && dotnet build && dotnet test
cd frontend && npm test -- --watch=false
```

## Prérequis

.NET SDK 10, Node.js 20.19+ (Node 22+ recommandé), Docker.
