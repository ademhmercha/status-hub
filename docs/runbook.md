# Runbook

Toutes les alarmes arrivent par email (sujet SNS `statushub-<env>-alarms`). Point de départ commun :

1. Ouvrir le tableau de bord CloudWatch **`statushub-<env>`** (région `eu-north-1`).
2. Relever le **correlation id** d'un log en erreur : c'est l'identifiant de trace X-Ray, identique dans le Scheduler, le Checker, l'Evaluator et le
   Notifier. Dans CloudWatch Logs Insights (groupes `/aws/lambda/statushub-<env>-*`) :
   ```
   fields @timestamp, service, level, message, correlation_id
   | filter correlation_id = "1-xxxxxxxx-xxxxxxxxxxxxxxxxxxxxxxxx"
   | sort @timestamp asc
   ```
3. Ouvrir la trace dans X-Ray (Traces → coller l'id) pour voir toute la chaîne.
4. Rollback d'une Lambda : workflow GitHub **Rollback** ou
   `aws lambda update-alias --function-name statushub-<env>-<fn> --name live --function-version <n> --profile teamchat`.

Commandes utiles (profil `teamchat`, région `eu-north-1`) :
```bash
aws logs tail /aws/lambda/statushub-prod-checker --since 15m --follow
aws sqs get-queue-attributes --queue-url <url> --attribute-names All
aws lambda get-alias --function-name statushub-prod-api --name live
```

---

## DLQ not empty
Alarme : `statushub-<env>-dlq-<checks|results|notifier>-not-empty`

**Signification** : un message a échoué 3 fois et a été isolé. Selon la file :
- `checks` : une vérification n'a pas pu être exécutée (crash du Checker, timeout, message invalide).
- `results` : un résultat n'a pas pu être enregistré (DynamoDB, droits IAM, bug de l'Evaluator).
- `notifier` : une alerte n'a pu être envoyée à aucun destinataire (SES refusé, webhook en erreur).

**Diagnostic**
1. Lire un message sans le supprimer :
   `aws sqs receive-message --queue-url <dlq-url> --max-number-of-messages 1 --visibility-timeout 0`
2. Chercher l'erreur dans les logs de la Lambda consommatrice (`level = "Error"`) avec le `correlation_id`.
3. Causes fréquentes : permission IAM (AccessDenied), identité SES non vérifiée ou compte SES en sandbox (destinataire non vérifié), DynamoDB throttling,
   webhook client qui répond ≥ 400.

**Résolution**
1. Corriger la cause (déploiement, IAM, vérifier l'adresse SES…).
2. Rejouer : `scripts/replay-dlq.sh <env> <checks|results|notifier>` (voir `docs/reliability.md`). Le rejeu est sûr : l'Evaluator et le Scheduler sont idempotents.
3. Si le message est invalide et inutile : le purger (`aws sqs purge-queue`), après avoir noté la cause.
4. L'alarme revient à OK quand la DLQ est vide.

## Lambda errors
Alarme : `statushub-<env>-<fonction>-errors` (≥ 1 erreur en 5 min)

1. Logs : `aws logs tail /aws/lambda/statushub-<env>-<fonction> --since 30m` ou Logs Insights `filter level = "Error"`.
2. Erreur de configuration (`Queues__...`, `DynamoDb__TableName` manquants) : vérifier les variables d'environnement de la fonction, redéployer.
3. `AccessDenied` : la politique IAM de la fonction (module `functions`) manque une action ; corriger Terraform, ne pas élargir à `*`.
4. Régression après un déploiement : **rollback immédiat** de l'alias `live`, puis corriger.
5. Le Checker ne lève pas d'erreur pour un site en panne (c'est un résultat normal) : une erreur Checker est un défaut de StatusHub.

## Lambda throttling
Alarme : `statushub-<env>-<fonction>-throttles`

Le compte (Free plan) a une limite de concurrence Lambda basse (souvent 10).
1. Voir `ConcurrentExecutions` de la fonction et la profondeur de la file `checks`.
2. Baisser `checker_max_concurrency` (module `stack`) ou réduire le nombre de moniteurs ; ne jamais réserver toute la concurrence du compte.
3. Demander une augmentation de quota (Service Quotas → Lambda → Concurrent executions) si l'usage est légitime.
4. Les messages throttlés reviennent dans la file ; surveiller `ChecksLate` : un throttling prolongé consomme le budget d'erreur.

## API 5xx
Alarme : `statushub-<env>-api-5xx` (≥ 5 réponses 5xx en 5 min)

1. Logs de `statushub-<env>-api` : `filter level = "Error"`, en-tête de réponse `X-Correlation-Id` pour retrouver une requête précise.
2. Journal d'accès API Gateway (`/aws/apigateway/statushub-<env>-access`) : `routeKey`, `status`, `error`.
3. Causes : DynamoDB indisponible ou throttling, exception non gérée, timeout (15 s), cold start répété sous charge.
4. Si c'est lié à un déploiement : rollback de l'alias `live` de la fonction `api`.
5. La page publique est mise en cache 30 s : une panne courte peut être partiellement masquée.

## Scheduler silent
Alarme : `statushub-<env>-scheduler-silent` (aucune invocation du Scheduler pendant 10 min)

**Plus aucune vérification ne part.** C'est le cas le plus grave : le burn rate ne le voit pas.
1. EventBridge Scheduler : la planification `statushub-<env>-every-minute` est-elle `ENABLED` ? (`schedule_enabled = false` la coupe volontairement.)
2. Rôle `statushub-<env>-scheduler-invoke` : droit `lambda:InvokeFunction` sur l'alias `live`.
3. L'alias `live` existe-t-il et pointe-t-il sur une version valide ?
4. Remettre en route : `terraform apply` (corrige les dérives) ou activer la planification dans la console.

## SLO burn rate
Alarmes : `statushub-<env>-slo-fast-burn` (urgent), `statushub-<env>-slo-slow-burn`

Des vérifications arrivent en retard (> 60 s) ou ne s'exécutent pas.
1. Tableau de bord, panneaux « Late and failed checks » et « Queue depth ». Si `checks` s'accumule : le Checker est trop lent ou throttlé.
2. **Checker lent** : `CheckLatencyMs` p95 élevée (sites lents, timeouts à 30 s) → un Checker occupe sa concurrence ; augmenter `checker_max_concurrency` ou réduire les timeouts.
3. **Throttling** : voir « Lambda throttling ».
4. **Messages perdus / DLQ `checks`** : voir « DLQ not empty ». La file `checks` expire les messages après 15 min : un long incident Checker se traduit par des vérifications perdues, donc comptées comme mauvaises.
5. **Cold starts** : panneau « Cold starts » ; le Checker en Native AOT démarre en quelques dizaines de ms, vérifier qu'il s'agit bien de l'artefact AOT (runtime `provided.al2023`).
6. Après correction, surveiller le budget restant : le fast burn retombe en ~1 h, le slow burn en ~6 h.

## Alerte d'incident non reçue (support)
1. L'incident existe-t-il ? (page du moniteur → Incidents.) Sinon : voir les logs de l'Evaluator (2 échecs consécutifs sont nécessaires).
2. Le message est-il dans la file `notifier` ou sa DLQ ?
3. SES en sandbox : le destinataire doit être une adresse vérifiée ; l'expéditeur (`SENDER_EMAIL`) aussi.
4. Webhook : vérifier la signature côté récepteur (`X-StatusHub-Signature`, `X-StatusHub-Timestamp` ; tolérance 5 min) et que l'URL répond en moins de 10 s.
5. Bouton « Envoyer un test » de la page Alertes : renvoie l'erreur exacte.
