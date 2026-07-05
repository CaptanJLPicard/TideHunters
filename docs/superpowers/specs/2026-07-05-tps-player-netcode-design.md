# TideHunters — TPS Player: Kontrol + Cinemachine Kamera + Blend Tree Animasyon + Server-Auth Netcode

**Tarih:** 2026-07-05
**Durum:** Onaylandı (kullanıcı: "Onayla, başla")

## Amaç
`Assets/Game/Prefabs/Player/Player.prefab` üzerindeki oyuncuyu TPS (nişancı/strafe) tarzı,
olabildiğince akıcı kontrol edilebilir hale getirmek; Cinemachine ile TPS kamera + Alt-freelook;
tüm animasyonları Blend Tree ile pürüzsüz sürmek; ve her şeyi (hareket + animasyon)
Netcode for GameObjects Host-Client mimarisinde **server-authoritative + client-side prediction/reconciliation**
ile senkron çalıştırmak.

## Onaylanan Kararlar
1. **Hareket tarzı:** Nişancı / strafe. Karakter her zaman kamera yaw'ına döner; 8 yönlü strafe blend tree; Alt = serbest bakış (body dondurulur).
2. **Kapsam:** Locomotion (Idle/Walk/Run 8-dir) + Jump/Fall + Swim + Emote (tam kapsam).
3. **Ağ yetkisi:** Server-authoritative.
4. **Server-auth derinliği:** Client-side prediction + reconciliation (tam AAA yaklaşımı).

## Ortam / Sürümler (doğrulandı)
- Unity 6000.3.15f1, URP 17.3.0
- Netcode for GameObjects 2.13.0 (paket cache: `com.unity.netcode.gameobjects@0f12e689d980`)
- Cinemachine 3.1.6, Input System 1.19.0
- Prefab kök: `NetworkObject`, `PlayerController`, `Animator`, `NetworkTransform` (→ kaldırılacak)
- Rig humanoid (Root/Hips/Spine_01...), animasyonlar Mixamo `Character@` retarget
- Input asset `Assets/InputSystem_Actions.inputactions` → "Player" map hazır: Move, Look, Sprint, Jump

## Varlıklar (Player animasyonları)
- Idle: Breathing Idle, Warrior Idle
- Walk: Walking, Walking Backwards, Left Strafe Walking, Right Strafe Walking
- Run: Running, Running Backward, Left Strafe, Right Strafe
- Jump: JumpForward, JumpStay
- Swim: SwimStay, Swimming
- Emote: Dancing Twerk, Hip Hop Dancing, Silly Dancing
- Avatar Mask: UpperBody, DownBody, JustArmsAndHands, JustHead

## Mimari — Dosyalar
- `PlayerController.cs` — ana `NetworkBehaviour` orkestratör; spawn, tick döngüsü, referanslar, CC yönetimi.
- `PlayerMotor.cs` — **saf** `Simulate(in MoveState, in InputCommand, float dt) → MoveState` (CharacterController hareketi, gravity, jump, swim). Owner-prediction + server aynı fonksiyonu çağırır.
- `PlayerNetTypes.cs` — `InputCommand` + `StatePayload` struct'ları (`INetworkSerializable`).
- `PlayerCameraRig.cs` — owner-only Cinemachine bağlama + mouse look + Alt freelook (yerel; ağa gitmez).
- `PlayerAnimator.cs` — state'ten Animator parametrelerini damping ile süren katman.

## Ağ Modeli — Prediction/Reconciliation
- Tick: NetworkManager `TickRate = 60`, sabit `dt = 1/60`. Hareket simülasyonu `NetworkTickSystem.Tick` üzerinde.
- `NetworkTransform` prefab'dan kaldırılır. Replikasyon: server → `NetworkVariable<StatePayload>` (server-write).
- **Owner (client):** her tick InputCommand üretir → yerelde anında `Simulate` (prediction) + ring-buffer'a yaz + `Rpc(SendTo.Server)` gönder.
- **Server:** input kuyruğunu işler, authoritative `Simulate`, `StatePayload` yayar.
- **Owner reconciliation:** authoritative state gelince o tick'in tahminiyle kıyasla; sapma > 0.1m → authoritative'e rewind + bekleyen input'ları replay. Görsel pop, `PlayerVisual` mesh error-offset ile ~birkaç frame yedirilir.
- **Remote:** StatePayload buffer + ~100ms render-delay interpolasyon (pos + yaw).
- **CharacterController enable:** `IsServer` → açık; client-only & IsOwner → açık; client-only & remote → kapalı.

## Kamera (Cinemachine 3.1.6)
- Sahnede tek `CinemachineCamera` + Main Camera'da `CinemachineBrain`. Body = `CinemachineThirdPersonFollow`.
- Owner spawn'da `Follow = CameraTarget` (prefab child, baş hizası). Remote dokunmaz.
- Mouse Look → CameraTarget yaw/pitch (pitch clamp −30..70), her frame LateUpdate.
- Normal: body yaw = kamera yaw. Alt (freelook): body dondurulur, ayrı offset kamerayı serbest döndürür; bırakınca offset ~0.25s'de 0'a lerp.

## Animator Controller (yeni `PlayerAnimator.controller`)
Parametreler: `Speed`(float), `MoveX`(float), `MoveY`(float), `Grounded`(bool), `Jump`(trigger), `IsSwimming`(bool), `EmoteId`(int).
- **Base Layer:** Locomotion = 1D(Speed) → { Idle, Walk-2D(MoveX,MoveY), Run-2D(MoveX,MoveY) }; Jump/Fall (JumpForward→JumpStay→Land); Swim (SwimStay/Swimming, IsSwimming gate); Emote (EmoteId, harekette iptal).
- **UpperBody Layer:** mask = `UpperBody`, weight 0 — gelecekteki attack/aim için hazır altyapı.
- Blend param'ları `SetFloat` + dampTime ≈ 0.1s.

## Senkronizasyon içeriği
`StatePayload`: `tick, position(Vector3), yaw(float), velocityY(float), grounded(bool), moveX(float), moveY(float), speed(float), isSwimming(bool), emoteId(int), jumpStamp(int)`.
`InputCommand`: `tick, moveX(float), moveY(float), yaw(float), sprint(bool), jump(bool), emoteId(int)`.
- Remote animasyon param'ları authoritative StatePayload'dan → %100 senkron.
- Owner kendi damped tahmininden → yerelde akıcı.

## Girdi
- Mevcut "Player" map kullanılır. Eklenecek: `FreeLook` (LeftAlt, Button/hold), `Emote1/2/3` (keys 3/4/5).
- Sabitler tweakable: walk 2.5, run 5.5 m/s, gravity −20, jump height 1.2m, mouse sens, reconcile threshold 0.1m, interp delay 100ms, waterLevelY.

## Prefab / Sahne Değişiklikleri (MCP)
1. Prefab: `NetworkTransform` sil → `CharacterController` ekle+boyutlandır → `CameraTarget` child ekle → Animator'a controller ata → yeni script component'leri ekle.
2. Input asset: FreeLook + Emote action'ları.
3. Sahne: CinemachineCamera + Brain + ThirdPersonFollow ayarları.
4. Animator controller + blend tree'ler MCP animator araçlarıyla.
5. NetworkManager TickRate = 60.

## Doğrulama
Multiplayer Play Mode (2 sanal oyuncu): host+client; iki taraftan hareket/strafe/jump/emote/swim/freelook; `screenshot-game-view` + `console-get-logs` ile senkron & hata; reconciliation sapma logları.

## Riskler / Notlar
- Prediction/reconciliation CharacterController determinizmine dayanır; ufak sapmalar threshold + smoothing ile gizlenir (prod-kabul, AAA-mükemmel değil).
- Uygulama tek canlı Editor'e karşı **sıralı** MCP çağrılarıyla; paralellik sadece bağımsız API-doğrulama alt-ajanlarında.
- Git: kullanıcı izni olmadan commit yok.
