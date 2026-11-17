# Résultats chiffrés

> **Lire d'abord : ce qui est mesuré et ce qui ne l'est pas.**
> Les sections 1 à 4 sont des **mesures réelles** faites le 17 novembre 2026 sur la machine de développement (Windows 11, 8 cœurs, Docker, DynamoDB Local en mémoire).
> Elles valident la logique applicative et la tenue en charge du code, **pas** les performances d'AWS (réseau, démarrages à froid, quotas de concurrence).
> La section 5 liste ce qui **doit être mesuré sur AWS** : les tableaux sont vides tant que vous n'avez pas lancé les scripts (aucun chiffre n'est inventé).
> Le lancement est décrit dans `loadtest/README.md` ; remplacez les cases « à mesurer ».

## 1. Tests automatiques

| Suite | Nombre | Résultat | Commande |
|---|---|---|---|
| Backend, tests unitaires (politique d'incident, Checker, Scheduler, Evaluator, Notifier, SSRF, signature webhook, métriques, lots) | 116 | tous verts | `dotnet test backend/StatusHub.sln` |
| Backend, intégration (repositories, API complète, idempotence) sur DynamoDB Local (Testcontainers) | 54 | tous verts | idem |
| Frontend (Vitest : composants, services, intercepteur, garde) | 38 | tous verts | `npm test -- --watch=false` |
| **Total** | **208** | | |

Compilation : 0 avertissement, 0 erreur (avertissements traités comme erreurs). L'analyse Native AOT du Checker (avertissements IL) ne signale rien ; la compilation native
complète nécessite Linux et se fait dans le conteneur Amazon Linux 2023 de la CI (`build-tools/aot/Dockerfile`).

## 2. Idempotence (preuve par tests, `IdempotencyTests`)

| Scénario | Résultat attendu et obtenu |
|---|---|
| Scheduler invoqué 2 fois pour la même minute | 1 message |
| Scheduler invoqué 8 fois en parallèle pour la même minute | 1 message |
| Résultat livré 2 fois par la file | 1 résultat, 1 agrégat, 1 incrément du compteur |
| Même résultat traité 6 fois en parallèle | 1 résultat |
| Panne de 3 min, chaque message livré 2 à 3 fois | 5 résultats, **1 incident**, événements `Opened` puis `Resolved` une seule fois |

## 3. Charge locale : 500 moniteurs pendant 5 minutes

500 moniteurs (25 comptes x 20) sur la cible de démo, intervalle 1 min, worker local (même code que les Lambdas), 25 utilisateurs de tableau de bord simulés.
Commande : `node loadtest/local-load.mjs --users 25 --per-user 20 --minutes 5`.

| Mesure | Valeur |
|---|---|
| Moniteurs | 500 |
| Vérifications planifiées / exécutées | 2 500 / 2 500 |
| Résultats enregistrés (somme des `checks24h`) | 2 500 |
| Débit | 8,33 vérifications/s (500 / min) |
| Latence d'une vérification p50 / p95 / p99 | 5 / 25 / 52 ms (cible locale) |
| Retard par rapport à la minute prévue p50 / p95 / p99 / max | 0,45 / 0,78 / 0,86 / 0,87 s |
| Vérifications en retard (> 60 s) | **0** |
| Part « à l'heure » (SLO ≥ 99,5 %) | **100 %** |
| Vérifications perdues | **0** |
| `GET /monitors` (20 moniteurs) p50 / p95 | 10 / 15 ms (582 appels) |

**Ce que le premier essai a révélé** (corrigé avant ces chiffres) :
1. Le ticker local n'était pas aligné sur la minute, ce qui faussait le retard mesuré (jusqu'à 82 s) : il déclenche désormais à chaque début de minute, comme EventBridge Scheduler.
2. Une ligne de log par vérification saturait la console (≈ 140 ms par ligne) : les vérifications saines ne journalisent plus rien (elles sont comptées en métrique), ce qui réduit aussi le coût CloudWatch.
3. Les limites de débit par défaut bloquaient le test (429) : elles sont relevées dans la configuration de développement uniquement.

## 4. Test de panne local

`node loadtest/failure-test.mjs --api http://localhost:5080 --mode local --email … ` : la cible de démo est coupée, MailHog reçoit l'alerte. Intervalle de 1 min.

| Étape | Durée mesurée |
|---|---|
| Cible coupée → incident ouvert (2 vérifications en échec) | 119,5 s |
| Cible coupée → incident visible par l'API | 120,6 s |
| Cible coupée → alerte envoyée | 119,5 s |
| Incident ouvert → alerte envoyée | **0,1 s** |
| Cible rétablie → incident résolu (2 succès) | 120,7 s |
| Cible rétablie → alerte de rétablissement | 118,8 s |

Lecture : le délai est dominé par la règle « 2 échecs consécutifs » et l'intervalle de 1 minute : entre ≈ 60 s (coupure juste avant une vérification) et ≈ 120 s (coupure juste après,
cas de cet essai). Une fois l'incident ouvert, l'alerte part en une fraction de seconde. Sur AWS s'ajoutent EventBridge, SQS et SES (section 5).

## 5. À mesurer sur AWS (tableaux à compléter)

Prérequis : dev déployé (`docs/ci-cd.md`), `AWS_PROFILE=teamchat`.

### 5.1 Charge : 500 moniteurs, 30 minutes

`k6 run … loadtest/load.js` puis `node loadtest/collect-results.mjs --env dev --start … --end …` (collé ici tel quel).

| Mesure | Valeur AWS |
|---|---|
| Vérifications planifiées / exécutées | à mesurer |
| Débit (vérifications / s) | à mesurer |
| Latence d'une vérification p50 / p95 / p99 | à mesurer |
| Retard après l'heure prévue p95 / p99 / max | à mesurer |
| Vérifications en retard (> 60 s) | à mesurer |
| Part « à l'heure » (objectif ≥ 99,5 %) | à mesurer |
| Vérifications perdues (planifiées − exécutées) | à mesurer |
| Erreurs / throttles Lambda, profondeur des DLQ | à mesurer |

Point d'attention : le quota de concurrence Lambda d'un compte gratuit (souvent 10) limite le Checker à `checker_max_concurrency` = 5 ; à 500 moniteurs il faut ≈ 50 invocations par minute
de ≈ 0,3 s chacune, soit une concurrence moyenne inférieure à 1, mais la rafale de début de minute peut en demander 5.

### 5.2 Panne : cible de démo coupée

`node loadtest/failure-test.mjs --api <url> --mode aws --env dev` (jeton Cognito dans `STATUSHUB_TOKEN`).

| Étape | Durée AWS |
|---|---|
| Cible coupée → incident ouvert | à mesurer |
| Cible coupée → alerte envoyée (ligne `Alert sent` du Notifier) | à mesurer |
| Incident ouvert → alerte envoyée | à mesurer |
| Cible rétablie → incident résolu | à mesurer |

### 5.3 Démarrages à froid : Native AOT contre JIT

`scripts/package-lambdas.sh --jit-checker` puis `loadtest/coldstart.sh dev 15 256` : même code, même mémoire (256 Mo), même rôle, 15 démarrages à froid par variante.

| Variante | Init min / p50 / p95 / max (ms) | Durée du handler p50 / p95 (ms) | Total p50 (ms) | Mémoire max (Mo) |
|---|---|---|---|---|
| Native AOT (`provided.al2023`) | à mesurer | à mesurer | à mesurer | à mesurer |
| JIT, self-contained, ReadyToRun | à mesurer | à mesurer | à mesurer | à mesurer |

Le tableau de bord (`statushub-dev`) donne aussi, pour les autres fonctions (JIT ReadyToRun sur le runtime géré `dotnet10`), le nombre de démarrages à froid, la durée d'init moyenne et maximale.

### 5.4 Coût mensuel réel

`loadtest/cost-report.sh dev <AAAA-MM>` (étiquettes `Project` / `Environment` activées dans Billing). Estimation en attendant : `docs/costs.md`.

| Configuration | Coût mensuel réel | Estimation (`docs/costs.md`) |
|---|---|---|
| 20 moniteurs, 1 vérification / min | à mesurer | ≈ 7 à 8 $ |
| 500 moniteurs, 1 vérification / min | à mesurer | ≈ 90 $ (DynamoDB ≈ 75 $) |
