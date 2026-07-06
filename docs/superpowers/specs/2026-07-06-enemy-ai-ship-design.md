# TideHunters — Düşman AI Gemi & Mürettebat Sistemi (Faz 1: SmallEnemyShip)

**Tarih:** 2026-07-06 · **Durum:** Onaylandı (tasarım) · **Kapsam:** yalnızca sahnedeki `SmallEnemyShip` + `Enemy_NPC` (diğer düşman gemilere dokunulmaz)

## Kararlar
- **NPC sayısı:** 5 (her `NPC_Points` çocuğuna bir tane).
- **Ağ:** Sunucu-yetkili / ağ-senkron. Beyin yalnız sunucuda (host) çalışır; sonuç istemcilere replikasyonla yansır.
- **Felsefe:** NPC = "Player'ın kopyası". `PlayerMotor.Simulate` + `PlayerAnimator` + `InputCommand`/`StatePayload` **doğrudan** yeniden kullanılır; ben yalnız beyin + sensör + sağlık + combat yazarım. NavMesh yok.

## Yeniden kullanılan mevcut sistemler (keşiften doğrulandı)
- `PlayerMotor.Simulate(cc, prev, InputCommand, dt, MotorConfig)` — saf, deterministik hareket. NPC bir yapay `InputCommand` üretir.
- `PlayerAnimator` (düz sınıf) — 8 param: `Speed, MoveX, MoveY, Grounded, IsSwimming, Falling, EmoteId(int), Jump(trigger)` (+ `GunSpeed`).
- Güvertede kalma: `PlayerController.CarryOnShip()`+`DetectGroundShip()` deseni (aşağı raycast + önceki-frame `worldToLocal`→şimdiki `localToWorld` deltası `cc.Move`; parenting YOK) → `DeckRider` yardımcısına çıkarılır.
- Gemi: `ShipController` (transform tabanlı, Gerstner buoyancy `OceanWaves.SurfaceOffsetY`, `_state` NetworkVariable replikasyonu). Enemy ship bunu **yeniden kullanır**.
- Aboard: `ShipController.Active`, `DeckBoardPoint`; `PlayerInteractor.FindShipToBoard` (raycast) → oyuncu düşman gemiye E ile çıkabilir (bedava). Sandık taşıyan tespiti: `PlayerInventory.IsCarryingChest`/`SelectedChest` (replike).
- Idle klipleri mevcut: `KneelingIDLE.anim`, `SittingIDLE.anim`, `Warrior Idle.anim`, `Carrying.anim`.

## Yeni dosyalar
- `IDamageable.cs`, `Health.cs` (NetworkBehaviour, 100 HP, `TakeDamageServer`, `OnChanged`, `OnDeath`), `ShipHealth.cs` (HealthVisuals sürer).
- `IShipPilot.cs` (gemi AI kancası), `EnemyShipAI.cs` (komutan beyin + pilot + top ateşi + NPC rol dağıtımı), `ShipCannon.cs` (menzilli top ateşi).
- `EnemyNpcController.cs` (ağ-senkron motor + anim + deck-carry + sağlık + sensör; komutandan rol alır).
- `DeckRider.cs` (deck-carry yardımcısı), `IAimSource.cs` (spine-aim arayüzü).
- `EnemyNpcAnimator.controller` (PlayerAnimator kopyası + Posture katmanı: 1=Kneel,2=Warrior,3=Sit,4=Carry).

## Değiştirilen mevcut dosyalar (geriye-uyumlu)
- `ShipController.cs`: opsiyonel `IShipPilot Pilot` kancası; sunucu sahipse + pilot varsa klavye yerine pilot girdisi. `AiControlled` bayrağı.
- `PlayerInteractor.cs`: `AiControlled` gemilerde "dümene geç" atlanır (ama aboard edilebilir).
- `PlayerCombat.cs`: `FireRpc`'e sunucu-tarafı hitscan → `IDamageable`'a 20 hasar.
- `PlayerSpineAim.cs`: `IAimSource` arayüzünden okuma (refactor); `PlayerController` implemente eder.
- `GameHUD.cs`: yerel oyuncu `Health.OnChanged` → HealthBar UI.
- `Player.prefab`: `Health` (100).
- Sahne `GameScene`: SmallEnemyShip kurulumu (NetworkObject, ShipController, EnemyShipAI, cannon+muzzle, ShipHealth), 5 NPC, `Canvas/GamePanel` altına HealthBar. Player gemisine hafif `ShipHealth` (top isabet feedback'i).
- **Dokunulmaz:** diğer enemy ship'ler, `DefaultNetworkPrefabs.asset` (NPC sahne-içi, gülle RPC-kozmetik → registrasyon gerekmez).

## Durum makinesi (komutan `EnemyShipAI` → asker `EnemyNpcController`)
| Durum | Tetik | Gemi | NPC |
|---|---|---|---|
| PEACEFUL | oyuncu gemisi uzak | burun oyuncuya, menzilde top | herkes noktasında idle (Kneel/Kneel/Warrior/Sit/Carry) |
| ALARM | oyuncu gemisi çok yakın (~25m) | top devam | idle bırak, güvertede sağa-sola dolaş (bazen dur), çarpışmadan |
| REPEL | oyuncu düşman gemide (aboard) | — | mesafe koru, arkası boşsa yan hareket, HeavyGun ateş |
| CHEST | biri sandığı aldı | — | sapmayla sandık taşıyanı öncele |
| COUNTER-BOARD | güvertede oyuncu kalmadı | — | kenardan suya atla, oyuncu gemisine aboard, saldır |
| RESET | oyuncu aboard etmeden uzaklaştı | burun çevir | noktaya dön, idle |

## Ayarlanabilir varsayılanlar
Top menzili 45m (min 8m), alarm 25m, top aralığı 3.5s, NPC ateş 1.2s + sapma, HP 100, hasar 20, hız player ile aynı.

## Doğrulama (aşamalı, her adımda screenshot/log)
1. Gemi yüzer + burun oyuncuya + top ateşler. 2. 5 NPC doğru idle. 3. Alarm dolaşma (düşmez, iç içe geçmez). 4. Aboard→REPEL ateş + HP UI. 5. Sandık önceliği + counter-board. 6. Konsol temiz.
