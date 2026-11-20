# BRUTAL FPS — un FPS fait avec le framework BRUTAL

Petit jeu de tir à la première personne construit **from scratch** sur le framework
[BRUTAL](https://github.com/ahwootech/BRUTAL) (Ahwoo / RocketWerkz), en un seul fichier C#
(`FpsBrutal.cs`, lancer avec `dotnet run` directement sur le fichier, ou l'exe précompilé).

Ce repo est **indépendant du fork de BRUTAL** : le framework est utilisé uniquement via ses
packages NuGet (`Ahwoo.Brutal.*`), aucune source de BRUTAL n'est copiée ici. Le fork local
(`E:\brutal-test`, clone de `ahwootech/BRUTAL`) sert à lire les samples et la source.

## Stack (packages NuGet `Ahwoo.Brutal.*`)

| Package | Rôle dans le jeu |
| --- | --- |
| `Ahwoo.Brutal.Glfw` | fenêtre + clavier/souris (curseur capturé, raw mouse motion) |
| `Ahwoo.Brutal.OpenGl` | rendu 3D OpenGL 4.1 core (shaders GLSL, VAO/VBO, draw calls) |
| `Ahwoo.Brutal.Jolt` | physique : collisions du joueur (capsule), raycasts de tir et de détection du sol, impulsions |
| `Ahwoo.Brutal.Numerics` | maths (float3/float4x4/floatQuat, matrices de vue/projection) |
| `Ahwoo.Brutal.Stb` | sauvegarde des captures d'écran du framebuffer (self-test) |
| `Ahwoo.Brutal.Core` | allocateurs/interop de base |

## Lancer le jeu

**Avec l'exécutable autonome (recommandé)** — `publish/FpsBrutal.exe` (2 Mo, compilé en NativeAOT,
les DLL natives `glad/glfw3/jolt/stb.dll` sont à côté de l'exe, ne les sépare pas) :

```sh
publish\FpsBrutal.exe
```

Ou depuis les sources (prérequis : .NET 10 SDK, installé ici dans `E:\dotnet`, dans le PATH) :

```sh
cd fps
dotnet run FpsBrutal.cs
# republier l'exe :
dotnet publish FpsBrutal.cs -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Contrôles : **WASD** bouger, **souris** viser, **clic gauche** tirer, **clic droit maintenu** = roue des armes,
**1-4** = arme directe, **R** = recharger, **espace** sauter, **shift** sprint, **E** = gadget,
**molette** = changer de gadget, **Échap** quitter. Le score/arme/gadget s'affichent dans le titre de la fenêtre,
et le **compteur de munitions (chargeur/réserve) est affiché à l'écran en bas à droite** (orange = rechargement,
rouge = chargeur vide).

### Roue des armes (4 armes hitscan)

Le **nom de chaque arme est écrit sur son secteur** (mini-font 5x7 vectoriel maison, sans dépendance).

| Slot | Arme | Cadence | Chargeur/réserve | Rechargement | Particularité |
| --- | --- | --- | --- | --- | --- |
| haut | Pistolet | 0.11 s | 12 / 60 | 0.9 s | 1 balle, dégâts 1 |
| gauche | SMG | 0.065 s | 30 / 120 | 1.4 s | rafale, dispersion faible |
| bas | Pompe | 0.75 s | 6 / 24 | 2.0 s | 6 pellets, grosse dispersion |
| droite | Sniper | 1.3 s | 5 / 20 | 2.2 s | 1 balle, dégâts 3, impulsion 45 |

Chaque arme a ses propres munitions. **R** recharge (auto aussi si le chargeur est vide en tirant) ;
changer d'arme annule le rechargement. Détruire une cible rapporte **+12 munitions** pour l'arme courante.

### Gadgets (touche E, molette pour cycler)

| Gadget | Effet | Cooldown |
| --- | --- | --- |
| Grenade | sphère physique lancée, explose après 2.5 s : onde de choc Jolt radiale (impulsion + dégâts de zone + flash plein écran) | 1 s |
| Dash | propulsion de 20 m/s dans la direction de la vue | 3 s |
| Jetpack | 1.2 s de poussée verticale (vy = 9) | 5 s |

## Self-test automatique

```sh
dotnet run FpsBrutal.cs -- --selftest
```

Un bot joue tout seul 15 s simulées (900 frames) : il avance, vise la cible la plus proche,
tire, saute, esquive les murs. Résultat attendu : `score > 0`, `hits > 0`, et 3 captures du
framebuffer (`frame30.png`, `frame150.png`, `frame600.png`) dans le dossier.

Dernier run : `SELFTEST DONE frames=901 score=30 shots=36 hits=32`.

## Ce que fait le jeu

- Joueur = capsule dynamique Jolt (gravité, saut détecté par raycast vers le bas, rotation verrouillée)
- Arène 40×40 : sol, 4 murs, 4 piliers, 3 plateformes (tous des boxes statiques Jolt)
- 8 cibles dynamiques avec **PV (5)** : chaque tir (raycast Jolt) leur enlève des dégâts et leur
  applique une impulsion. À 0 PV, la cible est **détruite** : elle explose en **10 débris physiques**
  (petits cubes Jolt lancés dans toutes les directions, visibles 2.5 s) et respawn 3 s plus tard.
  La grenade fait aussi des dégâts de zone (2 dégâts si proche) et peut détruire les cibles.
- Roue des armes 2D (anneau de 4 secteurs en NDC, blend alpha, pointeur souris) au clic droit maintenu
- Gadgets : grenade physique Jolt avec explosion à onde de choc radiale + flash écran, dash, jetpack
- Rendu : programme GLSL éclairé (Phong) pour la scène, programme "flat" pour le tracer/flash,
  programme 2D NDC (vec4, blend) pour viseur/roue/flash ; l'arme est un viewmodel de profil (view = identité)
- HUD : viseur + arme/gadget/score/FPS dans le titre de la fenêtre

## Licence

BRUTAL est sous licence MIT modifiée : `NOTICE.txt` (fourni à côté) doit accompagner le produit.
