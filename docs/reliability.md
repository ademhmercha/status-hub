# Fiabilité

## Idempotence de bout en bout

SQS et EventBridge Scheduler livrent **au moins une fois** : le même message peut arriver deux fois, et deux invocations simultanées du Scheduler sont possibles.
Chaque étape est donc protégée par une clé conditionnelle dans DynamoDB (`PutItem` avec `attribute_not_exists`, atomique) :

| Étape | Clé | Effet d'un doublon |
|---|---|---|
| Scheduler | `moniteur + minute` (`IDEM#<moniteur>#<yyyyMMddHHmm>`) | la vérification n'est publiée qu'une fois ; si la publication échoue, la clé est libérée pour permettre le rejeu |
| Evaluator | `eval#moniteur#minute` | le résultat n'est enregistré, agrégé et compté qu'une fois ; si le traitement échoue, la clé est libérée et la file redélivre |
| Notifier | aucune | seule étape qui peut envoyer un doublon (une alerte envoyée avant l'échec du lot) ; accepté plutôt que de risquer de perdre une alerte |

Les clés expirent après 1 h (TTL DynamoDB). Le Checker n'a pas besoin d'idempotence : refaire une vérification est inoffensif.

**Preuves** (`backend/tests/StatusHub.IntegrationTests/IdempotencyTests.cs`, sur DynamoDB Local) :
- Scheduler invoqué 2 fois, ou 8 fois **en parallèle**, pour la même minute : 1 seul message.
- Résultat livré 2 fois, ou traité 6 fois en parallèle : 1 seule ligne de résultat, 1 seul agrégat, compteur d'échecs incrémenté une fois.
- Panne de 3 minutes avec chaque message livré 2 à 3 fois : 5 résultats, **1 incident ouvert**, 1 événement `IncidentOpened`, 1 `IncidentResolved`.

## Traitement par lots et échecs partiels

- Les files sont lues par lots de 10 avec `ReportBatchItemFailures` : seuls les messages en échec sont remis dans la file, pas tout le lot.
- Le Checker exécute les 10 vérifications d'un lot **en parallèle** (un site lent ne retarde pas les autres) et envoie les 10 résultats en **une** requête
  `SendMessageBatch`. Si cet envoi échoue, tout le lot est rejoué (sans risque grâce à l'idempotence de l'Evaluator).
- Après 3 réceptions infructueuses, le message va en **DLQ** (une par file, conservation 14 jours) et déclenche l'alarme `dlq-not-empty`.

## Rejeu de la DLQ

```bash
scripts/replay-dlq.sh dev results          # demande confirmation, puis rejoue vers la file d'origine
scripts/replay-dlq.sh prod notifier --yes
```

Le script utilise la redrive native de SQS (`start-message-move-task`, 5 messages/s) et attend la fin. À faire **après** avoir corrigé la cause
(voir `docs/runbook.md`, « DLQ not empty »). Pour `checks`, mieux vaut purger : la file expire les messages après 15 minutes, une vérification
rejouée plus tard serait obsolète.

## Perte et retard : ce que le système garantit

- Une vérification **perdue** (jamais exécutée) est comptée comme mauvaise dans le SLO (dénominateur = vérifications planifiées).
- Le Scheduler ne retente pas un tick manqué (EventBridge Scheduler : 0 retry, âge max 60 s) : un tick tardif ne servirait à rien, la minute suivante repart normalement.
- Sans accès DynamoDB, le Scheduler échoue (alarme erreurs) ; sans accès SQS il libère ses clés d'idempotence et lève une erreur, la minute suivante reprend.

## Panne de la cible : temps de détection

Un incident s'ouvre après **2 échecs consécutifs** et se ferme après **2 succès** (évite les alertes sur un échec isolé). Avec un intervalle de 1 minute, le
meilleur cas est donc ≈ 1 à 2 minutes entre la panne et l'incident. Mesures réelles : `docs/results.md`.
