# CI/CD (GitHub Actions)

Dépôt : `ademhmercha/status-hub`. Aucune clé AWS n'est stockée : GitHub échange un jeton OIDC contre un rôle IAM temporaire.

## Workflows

| Fichier | Déclencheur | Rôle |
|---------|-------------|------|
| `ci.yml` | chaque pull request | build .NET (warnings = erreurs), tests unitaires + intégration (DynamoDB Local via Testcontainers) avec couverture, build + tests Angular, Trivy (fichiers, dépendances, secrets), `terraform fmt/validate`, tflint, Checkov, `terraform plan` de dev commenté sur la PR |
| `deploy.yml` | push sur `main` | construit **une fois** les artefacts, déploie dev, puis prod après approbation |
| `_deploy-env.yml` | appelé par `deploy.yml` | `terraform apply`, déploiement du frontend (S3 + invalidation CloudFront), smoke test |
| `rollback.yml` | manuel | remet l'alias `live` d'une Lambda sur une version précédente |

**Build once, deploy many** : `deploy.yml` publie deux artefacts (`lambda-<sha>`, `frontend-<sha>`) ; dev et prod téléchargent exactement les mêmes fichiers.
La configuration propre à l'environnement (URL de l'API, Cognito) est générée par Terraform dans `config.json` (S3), pas dans le build.

Le Checker est compilé en Native AOT dans `build-tools/aot/Dockerfile` (Amazon Linux 2023). Dans les plans de PR, il est remplacé par un
binaire factice : un changement de code du Checker n'apparaît donc pas dans le plan, mais il est bien déployé par `deploy.yml`.

## Réglages à faire dans GitHub

Ordre conseillé : d'abord `infra/bootstrap` (voir `infra/README.md`), car il produit les valeurs ci-dessous.

### 1. Environnements (Settings → Environments)

| Environnement | Reviewers | Branches autorisées |
|---------------|-----------|---------------------|
| `dev` | aucun | `main` uniquement |
| `production` | **vous** (Required reviewers), optionnel : Wait timer 5 min | `main` uniquement |

Les rôles IAM `statushub-deploy-dev` / `statushub-deploy-prod` n'acceptent que les jobs qui s'exécutent dans l'environnement
`dev` / `production` du dépôt. Restreindre ces environnements à `main` (Deployment branches) complète la protection : aucun autre
branche ne peut déployer.

### 2. Variables d'environnement (Settings → Environments → <env> → Environment variables)

| Variable | Valeur |
|----------|--------|
| `AWS_ROLE_ARN` | sortie `deploy_role_arns` du bootstrap (`dev` pour l'environnement dev, `prod` pour `production`) |
| `TF_STATE_BUCKET` | sortie `state_bucket` du bootstrap |
| `SENDER_EMAIL` | adresse expéditrice des alertes (SES vous envoie un lien de vérification) |
| `ALARM_EMAIL` | adresse qui reçoit les alarmes CloudWatch (confirmer l'abonnement SNS) |
| `ENABLE_SES`, `ENABLE_XRAY`, `ENABLE_CLOUDFRONT`, `ENABLE_PITR`, `ENABLE_OBSERVABILITY` | `false` pour tout service refusé par `check-free-plan.sh` (défaut : SES/X-Ray/CloudFront `true`, PITR `false`) |
| `ENABLE_GOOGLE`, `GOOGLE_CLIENT_ID` | optionnel (connexion Google) |

Secret d'environnement (optionnel) : `GOOGLE_CLIENT_SECRET`.

### 3. Variables du dépôt (Settings → Secrets and variables → Actions → Variables)

**`AWS_ROLE_ARN` doit aussi exister au niveau du dépôt** (mettez-y l'ARN du rôle dev) : c'est elle qui active les jobs de déploiement de `deploy.yml`.
Tant qu'elle est absente, le dépôt reste vert (seuls build et tests tournent ; dev et prod sont ignorés). Les variables d'environnement `AWS_ROLE_ARN`
de `dev` et `production` la remplacent dans leurs propres jobs.

Utilisées par le job `plan-dev` de `ci.yml` (il n'a pas d'environnement) : `AWS_PLAN_ROLE_ARN` (sortie `plan_role_arn`),
`TF_STATE_BUCKET`, `SENDER_EMAIL` et les `ENABLE_*` identiques à dev. Tant que `AWS_PLAN_ROLE_ARN` est vide, le job est ignoré.

### 4. Protection de la branche `main` (Settings → Branches)

- Require a pull request before merging
- Require status checks : `Backend (build, unit + integration tests, coverage)`, `Frontend (build + Vitest)`, `Trivy (files + dependencies)`, `Terraform (fmt, validate, tflint, Checkov)`
- Require branches to be up to date

### 5. Actions (Settings → Actions → General)

- Workflow permissions : **Read repository contents** (les workflows déclarent leurs propres permissions)
- Fork pull requests : *Require approval for all outside collaborators*

## Premier déploiement

1. Poussez la branche, ouvrez une PR : `ci.yml` tourne (le plan est ignoré tant que le bootstrap n'est pas fait).
2. Faites le bootstrap, renseignez les variables ci-dessus, fusionnez la PR.
3. `deploy.yml` déploie dev ; à la fin, un reviewer approuve `production`.
4. Vérifiez l'adresse SES (email de vérification) ; tant que le compte SES est en *sandbox*, les destinataires doivent aussi être vérifiés.

## Rollback

- **Immédiat** : onglet Actions → *Rollback* → choisir l'environnement, la fonction (et éventuellement la version). L'alias `live` est
  déplacé en quelques secondes. Corrigez ensuite par un commit (le prochain déploiement replacera l'alias sur la version déployée).
- Sinon, revert du commit fautif sur `main` : le pipeline redéploie.

## Plafonds de la CI

- Checkov est bloquant avec une liste de règles volontairement ignorées (pas de VPC, pas de clés KMS gérées, pas de WAF : voir `skip_check`
  dans `ci.yml`). Au premier passage, il peut signaler des règles supplémentaires : ajoutez-les avec une justification ou corrigez.
- La couverture frontend n'est pas publiée : le runner Vitest d'Angular 20 est expérimental et son rapport de couverture est inexploitable.
  Elle sera ajoutée après la montée en Angular 21 / Node 22.
