# Modèle de données DynamoDB (single-table)

Table `statushub-<env>` : clé `PK` (S) + `SK` (S), TTL sur l'attribut `ttl`, 2 index secondaires (GSI1, GSI2, projection ALL). Mode on-demand.
Les clés sont construites à un seul endroit : `Keys` dans `StatusHub.Infrastructure/Dynamo/DynamoSupport.cs`.

## 1. Patterns d'accès

| # | Besoin | Opération | Clé |
|---|--------|-----------|-----|
| A1 | Profil d'un compte | GetItem | `PK=USER#<uid>`, `SK=PROFILE` |
| A2 | Page de statut par slug | GetItem puis A1 | `PK=SLUG#<slug>`, `SK=SLUG` |
| A3 | Moniteurs d'un compte (+ limite 20) | Query | `PK=USER#<uid>`, `SK begins_with MON#` |
| A4 | Un moniteur d'un compte | GetItem | `PK=USER#<uid>`, `SK=MON#<id>` |
| A5 | Moniteurs dus à une minute donnée | 5 Query en parallèle | GSI2, `GSI2PK=DUE#<intervalle>#<décalage>` |
| A6 | Résultats d'un moniteur sur une période | Query | `PK=MON#<id>`, `SK BETWEEN RES#<t1> AND RES#<t2>` |
| A7 | Incidents d'un moniteur (récents d'abord) | Query `ScanIndexForward=false` | `PK=MON#<id>`, `SK begins_with INC#` |
| A8 | Incidents ouverts d'un compte | Query | GSI1, `GSI1PK=OPEN#<uid>` |
| A9 | Agrégats journaliers (dispo. 24 h/7 j/30 j/90 j) | Query | `PK=MON#<id>`, `SK BETWEEN AGG#<d1> AND AGG#<d2>` |
| A10 | Abonnements d'alerte d'un compte | Query | `PK=USER#<uid>`, `SK begins_with SUB#` |
| A11 | Idempotence (moniteur + minute) | PutItem conditionnel | `PK=IDEM#<moniteur>#<yyyyMMddHHmm>`, `SK=IDEM` |

Toute lecture côté API passe par `USER#<uid>` (identifiant du JWT) : un utilisateur ne peut pas atteindre les données d'un autre.

## 2. Items

| Entité | PK | SK | Attributs principaux | Index |
|--------|----|----|----------------------|-------|
| Compte | `USER#uid` | `PROFILE` | email, slug, createdAt | |
| Slug | `SLUG#slug` | `SLUG` | userId | |
| Moniteur | `USER#uid` | `MON#id` | name, url, method, interval (1-5), expectedStatus, keyword?, timeout, active, status, failures, successes, openIncidentId?, lastCheckedAt? | GSI2 si `active` |
| Résultat | `MON#id` | `RES#yyyyMMddHHmmssfff` | success, statusCode?, latencyMs, error, errorMessage?, **ttl = +30 jours** | |
| Incident | `MON#id` | `INC#<id>` (id triable par date) | accountId, openedAt, resolvedAt?, cause | GSI1 tant qu'ouvert |
| Agrégat | `MON#id` | `AGG#yyyy-MM-dd` | cnt, ok, sumLat, h0..h12 (histogramme de latence) | |
| Abonnement | `USER#uid` | `SUB#id` | alertType (Email/Webhook), target, secret?, active | |
| Idempotence | `IDEM#clé` | `IDEM` | ttl (1 h) | |

## 3. Index secondaires

- **GSI2 : moniteurs dus.** Chaque moniteur actif a `GSI2PK = DUE#<intervalle>#<décalage>` où `décalage = hash(id) mod intervalle`
  (hash stable, pas `string.GetHashCode`). À la minute `m = epoch/60`, un moniteur d'intervalle `i` est dû si `m mod i = décalage` :
  le Scheduler interroge 5 partitions (`i = 1..5`, décalage `m mod i`). Les moniteurs d'un même intervalle sont étalés sur
  les minutes. Index **creux** : un moniteur en pause n'y figure pas (on retire `GSI2PK/GSI2SK`).
- **GSI1 : incidents ouverts.** Index creux : `GSI1PK=OPEN#<uid>` n'existe que tant que l'incident est ouvert ;
  `ResolveAsync` supprime ces attributs.
- **Slug : pas d'index.** Écart assumé par rapport à la demande : un item `SLUG#<slug>` lu par clé primaire est moins cher
  qu'un GSI et garantit l'unicité par transaction (`TransactWriteItems` avec `attribute_not_exists`). Changer de slug
  = une transaction (réserve le nouveau, libère l'ancien, met à jour le profil).

## 4. Agrégats : pourquoi un histogramme

p50/p95 ne s'obtiennent pas par moyenne. Chaque vérification fait un `UpdateItem ... ADD cnt, ok, sumLat, h<bucket>` **atomique**
(pas de lecture-modification-écriture, donc sûr en concurrence). Les percentiles sont lus sur 13 seuils
(50, 100, 200, 300, 500, 750, 1000, 1500, 2000, 3000, 5000, 10000 ms, au-delà) ; la valeur retournée est la borne haute du seuil.

## 5. Choix et limites

- Le Checker n'accède pas à la base : le message SQS contient la configuration du moniteur.
- L'état d'un moniteur (compteurs d'échecs/succès, incident ouvert) est écrit par l'Evaluator. Une modification simultanée par
  l'API (remplacement complet de l'item) est possible mais sans conséquence grave (compteurs recalculés au contrôle suivant).
- Résultats : TTL 30 jours (DynamoDB supprime sous ~48 h ; les lectures filtrent par période). Agrégats et incidents sont conservés.
- Les secrets de webhook sont stockés en clair dans l'item (table chiffrée au repos par DynamoDB) et ne sont jamais renvoyés par l'API après création.
