# Tests de charge et de panne

Tout se lance **à la main** (rien n'est exécuté par la CI ni par Claude sur votre compte AWS). Les résultats se consignent dans `docs/results.md`.

| Outil | Rôle | Cible |
|---|---|---|
| `local-load.mjs` | 500 moniteurs sur la cible de démo, mesure locale (DynamoDB Local) | machine locale |
| `load.js` (k6) | 500 moniteurs pendant 30 min + trafic API | AWS (ou local) |
| `collect-results.mjs` | débit, p95, retards, pertes depuis CloudWatch pour une fenêtre | AWS |
| `failure-test.mjs` | coupe la cible de démo : délai incident + alerte | local ou AWS |
| `coldstart.sh` | cold starts Native AOT vs JIT, même code | AWS |
| `users.sh` | crée/supprime les 25 utilisateurs Cognito du test | AWS |
| `cost-report.sh` | coût réel du mois par service (Cost Explorer) | AWS |

## 1. Test de charge sur AWS (500 moniteurs, 30 min)

Prérequis : dev déployé, k6 installé. 500 moniteurs = 25 comptes x 20 (limite par compte).

```bash
export AWS_PROFILE=teamchat
USER_PASSWORD='Un-Mot-De-Passe-1' loadtest/users.sh create dev 25

k6 run -e API_URL=$(terraform -chdir=infra/envs/dev output -raw api_endpoint) \
       -e AUTH_MODE=cognito -e COGNITO_CLIENT_ID=$(terraform -chdir=infra/envs/dev output -raw user_pool_client_id) \
       -e USER_PASSWORD='Un-Mot-De-Passe-1' -e DURATION=30m loadtest/load.js
```

k6 affiche l'heure de début et de fin ; ensuite :

```bash
node loadtest/collect-results.mjs --env dev --start <début> --end <fin>      # tableau markdown pour docs/results.md
loadtest/users.sh delete dev 25
```

Pendant le test, regardez le tableau de bord `statushub-dev` et vérifiez qu'aucune alarme (DLQ, throttling, burn rate) ne se déclenche.
Avec le quota de concurrence Lambda d'un compte gratuit (souvent 10), le Checker est limité à 5 invocations simultanées (`checker_max_concurrency`) :
c'est justement ce que ce test valide.

## 2. Test de panne

```bash
# local
node loadtest/failure-test.mjs --api http://localhost:5080 --mode local --email you@example.com
# AWS (jeton = ID token Cognito)
STATUSHUB_TOKEN=... node loadtest/failure-test.mjs --api <url api> --mode aws --env dev
```

## 3. Cold starts AOT vs JIT

```bash
scripts/package-lambdas.sh --jit-checker      # Docker requis (AOT sous Amazon Linux 2023)
loadtest/coldstart.sh dev 15 256              # 15 démarrages à froid par variante, 256 Mo
```

Le script crée deux fonctions temporaires (même rôle, mêmes variables, même mémoire), force des démarrages à froid en modifiant une variable d'environnement,
lit `Init Duration` dans la ligne `REPORT`, affiche le tableau et supprime les fonctions.

## 4. Coût réel

```bash
loadtest/cost-report.sh dev 2026-12
```

## Test local (sans AWS)

```bash
docker compose up -d dynamodb mailhog
cd backend && ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/StatusHub.Api --no-launch-profile -c Release
node loadtest/local-load.mjs --users 25 --per-user 20 --minutes 5
```

Il mesure la logique applicative et la machine (DynamoDB Local, réseau local) : ce n'est **pas** une mesure d'AWS.
