# Coûts

> **Statut des chiffres** : ce document contient (1) un **modèle de calcul** à partir du nombre d'opérations réellement émises par le code, avec des tarifs
> publics **indicatifs** d'`eu-north-1` à vérifier, et (2) la procédure pour obtenir le **coût mesuré** (Cost Explorer). Aucun chiffre ci-dessous n'est une
> facture : tant que l'application n'a pas tourné un mois sur AWS, ce sont des estimations. Remplacez-les par la sortie de `loadtest/cost-report.sh`.

## Étiquettes de coût

Toutes les ressources reçoivent `Project=StatusHub`, `Environment=<dev|prod>` et `ManagedBy=terraform` (`default_tags` du provider AWS).
Pour les voir dans Cost Explorer : console **Billing → Cost allocation tags → activer `Project` et `Environment`** (24 h de délai), puis :

```bash
loadtest/cost-report.sh dev 2026-12      # coût réel du mois, par service (≈ 0,01 $ par appel Cost Explorer)
```

Alarme de sécurité conseillée : un budget AWS (Billing → Budgets) à 10 $ avec alerte email à 80 %, hors Terraform pour ne pas dépendre de l'état.

## Opérations par vérification (après optimisation par lots)

Chaque minute, le Scheduler publie les vérifications dues par paquets de 10 (`SendMessageBatch`) ; le Checker en traite 10 en parallèle et envoie ses 10 résultats en une
requête ; l'Evaluator traite 10 résultats par invocation.

| Poste | Par vérification | Remarque |
|---|---|---|
| DynamoDB écritures | **5 WRU** | résultat, agrégat, état du moniteur, clé d'idempotence Scheduler, clé d'idempotence Evaluator |
| DynamoDB lectures | ≈ 1 RRU | lecture du moniteur par l'Evaluator (+ requête d'index par minute côté Scheduler) |
| SQS | ≈ 0,6 requête | 6 requêtes par lot de 10 : envoi, réception, suppression sur chacune des 2 files |
| Lambda | ≈ 0,2 invocation | Checker + Evaluator, un lot de 10 par invocation, + 1 Scheduler par minute |
| Logs | ≈ 0 octet | une vérification saine n'écrit aucun log (elle est comptée en métrique) ; seuls les échecs, les incidents et les erreurs sont journalisés |

Avant l'envoi par lots, SQS comptait ≈ 6 requêtes **par vérification** : le regroupement divise cette ligne par 10.

## Hypothèses du modèle

- 30 jours, 1 vérification par minute et par moniteur : **43 200 vérifications / mois / moniteur** → 20 moniteurs = 864 000 ; 500 moniteurs = 21 600 000.
- Lots pleins (les vérifications d'une minute sont publiées en même temps, donc regroupées) ; fenêtre de regroupement 2 s.
- Lambda : Checker 256 Mo x86_64 (Native AOT), ≈ 0,3 s par lot ; Evaluator 256 Mo arm64, ≈ 0,4 s par lot.
- SQS : en plus des lots, chaque file lue par Lambda reçoit des requêtes d'écoute (long polling) même au repos : ≈ 1,9 M requêtes / mois pour 3 files.
- Tarifs indicatifs (USD) : Lambda 0,20 $ / M requêtes, 0,0000166667 $ (x86) ou 0,0000133334 $ (arm64) par Go-s ; SQS 0,40 $ / M requêtes ; DynamoDB à la demande ≈ 0,70 $ / M écritures
  et ≈ 0,14 $ / M lectures ; CloudWatch Logs ≈ 0,57 $ / Go ingéré (négligeable ici, voir « Logs ») ; métrique personnalisée 0,30 $ / mois ; alarme 0,10 $ / mois.
- Quotas gratuits permanents pris en compte : Lambda 1 M requêtes + 400 000 Go-s, SQS 1 M requêtes, CloudWatch 5 Go de logs, 10 métriques, 10 alarmes, 3 tableaux de bord, X-Ray 100 000 traces.
  **DynamoDB à la demande n'a pas de quota gratuit de débit** (seulement 25 Go de stockage).

## Estimation mensuelle

| Service | 20 moniteurs | 500 moniteurs | Commentaire |
|---|---|---|---|
| DynamoDB écritures (5 WRU / vérif.) | 4,3 M → **≈ 3,0 $** | 108 M → **≈ 75 $** | premier poste de coût |
| DynamoDB lectures + stockage | ≈ 0,15 $ | ≈ 3 $ | résultats expirés à 30 jours |
| SQS | ≈ 0,6 $ | ≈ 5,6 $ | requêtes d'écoute incluses |
| Lambda requêtes + durée | ≈ 0 $ (quota gratuit) | ≈ 0,7 $ (≈ 380 000 Go-s : sous le quota gratuit de durée) | |
| CloudWatch (logs, 20 métriques, ≈ 17 alarmes, 1 tableau de bord) | ≈ 3,5 $ | ≈ 4 $ | les métriques et alarmes sont un coût fixe ; les logs ne coûtent presque rien |
| API Gateway, Cognito, S3, CloudFront, EventBridge, SNS, X-Ray | < 0,5 $ | < 1 $ | trafic faible, quotas gratuits |
| **Total estimé** | **≈ 7 à 8 $ / mois** | **≈ 90 $ / mois** | |

**Lecture honnête** : la contrainte « rester dans les quotas gratuits » est tenue pour le calcul (Lambda) mais **pas** pour DynamoDB, CloudWatch ni SQS dès que l'on surveille
toutes les minutes. Les chiffres montrent aussi que la partie fixe (métriques et alarmes personnalisées) pèse plus que le calcul pour un petit compte.

## Leviers (du plus au moins rentable)

1. **Intervalle de 5 minutes par défaut** : tout le coût variable est divisé par 5 (20 moniteurs : ≈ 4 à 5 $ / mois au total).
2. **N'écrire l'état du moniteur que s'il change** (≈ −20 % d'écritures DynamoDB) : au prix d'un « dernier contrôle » moins précis.
3. **Résultats : ne conserver que les échecs et un échantillon** (les agrégats suffisent pour la disponibilité et les percentiles) : jusqu'à −40 % d'écritures.
4. **DynamoDB en capacité provisionnée** quand la charge est stable (le quota gratuit de 25 WCU / 25 RCU s'applique) : 25 WCU = 25 écritures/s ; 500 moniteurs = 8,3 vérifications/s x 5 = 42 WCU en moyenne.
5. Retirer les alarmes par fonction non essentielles (erreurs/throttles x5 = 10 alarmes) ou les regrouper en alarmes composites.
6. Réduire à 7 jours la rétention des logs en dev (`log_retention_days`).

## Ce qui est volontairement évité

Pas de NAT Gateway, pas de VPC pour les Lambdas, pas d'EC2/RDS, pas de WAF, pas de clé KMS gérée (0,03 $ à 1 $ chacune), pas de réplication DynamoDB globale. CloudFront en `PriceClass_100`.
