# Infrastructure StatusHub (Terraform)

Région `eu-north-1`, profil AWS local `teamchat`. Terraform >= 1.10, provider `aws ~> 6`.
**Rien n'est appliqué par le code ou par Claude** : vous lancez `plan` / `apply` vous-même.

```
infra/
  scripts/check-free-plan.sh   teste quels services votre compte autorise (crée puis supprime)
  bootstrap/                   bucket de state + OIDC GitHub + rôles IAM de déploiement (état local)
  modules/
    data         DynamoDB on-demand, TTL, PITR optionnel
    messaging    SQS checks/results/notifier + DLQ, bus EventBridge, règle incidents -> file notifier
    functions    5 Lambdas, un rôle IAM par fonction, X-Ray, logs 14 j, versions + alias "live"
    api          HTTP API + authorizer JWT Cognito, routes publiques
    auth         Cognito (+ Google en option)
    web          S3 privé + CloudFront (OAC), fallback SPA
    scheduler    EventBridge Scheduler (chaque minute)
    email        identité SES
    stack        assemble tout un environnement
  envs/dev, envs/prod          appellent `stack` (prod : protection contre la suppression)
```

## Ordre d'exécution (à la main)

1. **Vérifier le compte** : `AWS_PROFILE=teamchat ./infra/scripts/check-free-plan.sh`. Pour chaque `REFUSE`, désactivez la variable
   indiquée entre parenthèses dans `infra/envs/<env>/terraform.tfvars` (`enable_ses`, `enable_xray`, `enable_cloudfront`, `enable_pitr`).
2. **Bootstrap** (une fois) :
   ```bash
   cd infra/bootstrap
   terraform init && terraform plan -out=tfplan   # relisez le plan
   terraform apply tfplan
   terraform output
   ```
   Si le compte a déjà un fournisseur OIDC GitHub : `-var create_oidc_provider=false`.
   Conservez `terraform.tfstate` du bootstrap (ou migrez-le vers le bucket créé).
3. **Environnement** :
   ```bash
   cd infra/envs/dev
   cp backend.hcl.example backend.hcl            # renseignez le bucket (sortie du bootstrap)
   cp terraform.tfvars.example terraform.tfvars   # renseignez sender_email
   terraform init -backend-config=backend.hcl
   terraform plan -out=tfplan -var artifacts_dir=<dossier des zips>
   ```
   En pratique c'est la CI (`deploy.yml`) qui construit les zips et applique. Voir le README racine et `docs/`.

## Choix de moindre privilège

- Un rôle IAM par Lambda, limité à sa file, à ses index DynamoDB et à ses événements. Le Scheduler ne peut écrire que les clés `IDEM#*`.
- Rôles de déploiement : un par environnement, `sub` OIDC = `repo:ademhmercha/status-hub:environment:<env>`. Leurs droits sont
  limités aux ressources nommées `statushub-<env>-*` (sauf les API qui n'acceptent pas de ressource : CloudFront, API Gateway, Cognito à la création).
- Rôle `statushub-plan-dev` : lecture des métadonnées uniquement, pour les `terraform plan` des pull requests.
- Aucun secret dans le dépôt : le secret Google passe par `TF_VAR_google_client_secret` (secret d'environnement GitHub).

## Rollback d'une Lambda

Chaque déploiement publie une version ; l'alias `live` pointe dessus.
```bash
aws lambda update-alias --function-name statushub-prod-api --name live --function-version <version précédente> --profile teamchat
```
Puis corrigez par un nouveau déploiement (Terraform remettra l'alias sur la version de l'artefact déployé).

## Coûts

Pas de NAT, pas de VPC, pas d'EC2/RDS. Lambdas arm64, DynamoDB on-demand, SQS, API Gateway HTTP API, CloudFront PriceClass_100.
`schedule_enabled = false` met toutes les vérifications en pause.
