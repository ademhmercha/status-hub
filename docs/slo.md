# SLO StatusHub

## Objectif

> **99,5 % des vérifications sont exécutées moins de 60 secondes après leur heure prévue, sur 30 jours glissants.**

Ce que l'on mesure : la **ponctualité de StatusHub**, pas la disponibilité du site surveillé. Un site en panne détecté à l'heure est une
vérification réussie du point de vue du SLO.

## Indicateur (SLI)

```
SLI = ChecksOnTime / ChecksScheduled
```

| Métrique (namespace `StatusHub/<env>`) | Émise par | Signification |
|---|---|---|
| `ChecksScheduled` (Service=scheduler) | Scheduler | vérifications qui **doivent** tourner cette minute (publiées maintenant ou déjà publiées par un appel précédent) |
| `ChecksOnTime` (Service=checker) | Checker | vérifications exécutées au plus 60 s après la minute prévue |
| `ChecksLate` (Service=checker) | Checker | exécutées avec plus de 60 s de retard |

Le dénominateur vient du Scheduler et non du Checker : **une vérification perdue** (message supprimé, Lambda en panne, file bloquée) est
comptée comme mauvaise, car elle est planifiée mais jamais exécutée. Le délai est `CheckedAt - minute prévue`
(`CheckDelaySeconds` en donne la distribution).

## Budget d'erreur

Marge : 0,5 % des vérifications, soit environ **216 vérifications en retard ou perdues par mois et par moniteur d'intervalle 1 min**
(43 200 vérifications / mois / moniteur × 0,5 %). Pour 20 moniteurs de 1 min : 864 000 vérifications / 30 j, soit 4 320 en retard ou perdues tolérées.

Le tableau de bord `statushub-<env>` affiche :
- **SLI par heure** avec le seuil 99,5 % ;
- **Budget d'erreur restant (30 jours)** : `100 − 100 × (planifiées − à l'heure) / (0,005 × planifiées)`. 100 % = rien consommé, 0 % = budget épuisé, négatif = SLO violé.

## Alarmes de burn rate

`burn rate = taux d'erreur / budget` : 1 consomme exactement le budget en 30 jours.

| Alarme | Fenêtre | Seuil | Sens | Action |
|---|---|---|---|---|
| `statushub-<env>-slo-fast-burn` | 1 h | > 14,4 | 2 % du budget mensuel consommé en 1 h | **urgent** : voir runbook |
| `statushub-<env>-slo-slow-burn` | 6 h | > 6 | 5 % du budget mensuel consommé en 6 h | à traiter dans la journée |

Les fenêtres de moins de 30 (rapide) ou 100 (lente) vérifications sont ignorées, pour ne pas alerter pour une seule vérification perdue sur un
petit compte. Les notifications partent vers le sujet SNS `statushub-<env>-alarms` (email).

Complément : `statushub-<env>-scheduler-silent` se déclenche si le Scheduler n'est plus invoqué pendant 10 minutes. Sans cela, plus aucune
vérification n'est planifiée, donc le burn rate resterait à 0.

## Ce que le SLO ne couvre pas

- Le temps entre la panne réelle et l'alerte (voir `docs/results.md` : mesure « panne → incident → alerte »).
- La disponibilité de la page de statut et de l'API (suivies par les alarmes API 5xx et le tableau de bord).
- La délivrabilité des emails (SES sandbox, filtres anti-spam).

## Réviser l'objectif

Le seuil est une variable du module (`slo_target`, `fast_burn_rate`, `slow_burn_rate` dans `infra/modules/observability`). Si les 30 premiers jours montrent un budget
consommé de façon chronique, corriger la cause plutôt que d'abaisser l'objectif.
