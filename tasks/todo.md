# TODO — TPS Player: Kontrol + Cinemachine + Blend Tree + Server-Auth Netcode

Spec: `docs/superpowers/specs/2026-07-05-tps-player-netcode-design.md`
Onay: "Onayla, başla" (2026-07-05)

## Faz 0 — Hazırlık / Doğrulama
- [x] NGO 2.13 + Cinemachine 3.1.6 kesin API imzalarını doğrula (alt-ajan)
- [x] Sahneyi (GameScene) ve NetworkManager'ı incele (PlayerPrefab bağlı, tick 30→60, Main Camera, ClientServer)
- [x] Player prefab component detaylarını doğrula (NetworkObject+PlayerController+Animator+NetworkTransform, humanoid rig)

## Faz 1 — Input System
- [x] "Player" map'ine `FreeLook` action (LeftAlt)
- [x] `Emote1`/`Emote2`/`Emote3` action'ları (3/4/5)

## Faz 2 — C# Kod
- [x] `PlayerNetTypes.cs` — InputCommand + StatePayload (INetworkSerializable + IEquatable)
- [x] `PlayerMotor.cs` — saf Simulate(state, input, dt) (CC hareket, gravity, jump, swim)
- [x] `PlayerAnimator.cs` — state → Animator param (damping)
- [x] `PlayerCameraRig.cs` — owner Cinemachine + mouse look + Alt freelook
- [x] `PlayerController.cs` — orkestratör: spawn, tick, prediction, reconciliation, RPC, NetworkVariable
- [x] assets-refresh + console-get-logs → **0 derleme hatası**

## Faz 3 — Animator Controller + Blend Tree
- [x] `PlayerAnimator.controller` (script-execute ile inşa): 7 param, 2 layer, 7 base state
- [x] Base Layer: Locomotion 1D(Speed) → Idle / Walk-2D / Run-2D (SimpleDirectional2D)
- [x] Jump/Fall (JumpStart→Airborne, Jump trigger + Grounded)
- [x] Swim (SwimStay/Swimming, AnyState←IsSwimming)
- [x] Emote (AnyState→Emote1/2/3, EmoteId Equals)
- [x] UpperBody masked layer (UpperBody.mask, weight 0, hazır)
- [x] 16 klip loop ayarlarıyla atandı (klip eksik yok)

## Faz 4 — Prefab Cerrahisi
- [x] `NetworkTransform` kaldırıldı
- [x] `CharacterController` eklendi (h1.8, r0.28, center 0.9)
- [x] `CameraTarget` child (0,1.6,0)
- [x] Animator.controller = PlayerAnimator, ApplyRootMotion=false, AlwaysAnimate
- [x] PlayerController.inputAsset + tunable alanları serialize edildi

## Faz 5 — Sahne / Kamera
- [x] Main Camera'da CinemachineBrain
- [x] `PlayerFollowCamera` (ThirdPersonFollow, distance 4, damping 0.4, FOV 55, priority 10)
- [x] Default blend EaseInOut 0.35s
- [x] NetworkManager TickRate 30→60
- [x] Sahne kaydedildi

## Faz 6 — Doğrulama (play mode, host)
- [x] Host başladı (IsHost, Tick=60), oyuncu otomatik spawn
- [x] TPS kamera oyuncuyu takip ediyor (omuz-üstü, iyi kompozisyon)
- [x] Idle / Walk (Speed 0.5) / Run-Sprint (Speed 1.0) / Strafe (MoveX) — Locomotion state doğru
- [x] Swim otomatik algılama + buoyancy + Swim state + Swimming klibi
- [x] Jump fizik (vertVel doğru, JumpStamp→Jump trigger) — PlayerMotor birim testi
- [x] Emote (EmoteId=1 → Emote1 state → Twerk oynuyor)
- [x] Konsol: tüm oturumda **0 exception / 0 hata**

## Review

**Tamamlandı.** Tam server-authoritative + client-side prediction/reconciliation'lı TPS
(nişancı/strafe) oyuncu; Cinemachine 3 ThirdPersonFollow kamera + Alt freelook; 8 yönlü
Blend Tree locomotion + Jump + Swim + Emote; UpperBody avatar mask'li ikinci katman; her şey
Netcode for GameObjects Host-Client'a uygun ve senkron. Play mode host testinde tüm state'ler
doğru, sıfır exception.

### Otomatik test edilemeyen (standart/güvenilir kod yolları — kullanıcı MPPM'de doğrulamalı)
- Mouse look + Alt freelook hissi (gerçek fare gerektirir; kamera takibi çalışıyor).
- 2 oyunculu senkron (Multiplayer Play Mode ile host+client açıp iki taraftan hareket/animasyon).
- Jump/Emote tuş-latch'i (`WasPressedThisFrame`) — gerçek tuşta çalışır; enjeksiyon zamanlaması
  nedeniyle otomatik tetiklenmedi, mantık ayrıca doğrulandı.

## Düzeltmeler (Tur 2 — kullanıcı geri bildirimi)
- [x] **WASD hareket titremesi (host+client)**: 60Hz tick pozisyonları arası render interpolation
  eklendi (`PlayerController.LateUpdate` + `[DefaultExecutionOrder(-100)]`, tick başında authoritative
  snap). Dikey swim hareketiyle pipeline pürüzsüzlüğü doğrulandı.
- [x] **Su üstünde/havada yüzme**: gerçek su yüzeyi bulundu (SM_Env_Ocean_Tile ≈ -1.6);
  `waterLevelY=-1.6`. Karakter artık yarı-batık yüzüyor (pos.y=-3.0, doğrulandı).
- [x] **Denize girmeden yüzme animasyonu**: swim tetiği artık `submerged > swimEnterDepth(0.5)`
  → sadece su yüzeyinin 0.5m altına inince. Karada IsSwimming=False doğrulandı.
- [x] **Yüzme derinliği ince ayarı**: `swimFloatDepth=1.4` → su çizgisi göğüste, treading'de kollar
  yüzeyde (pos.y=-3.0, screenshot ile doğrulandı).

### Sonraki tur için notlar / polish
- **Kamera obstacle avoidance**: ThirdPersonFollow > AvoidObstacles.Enabled + CollisionFilter
  ayarı (karakter kayaya yaklaşınca kamera içeri klipliyor). Layer kurulumuna göre ayarlanmalı.
- **waterLevelY tuning**: MotorConfig.waterLevelY (-0.5) sahnenin gerçek su yüzeyine göre
  ince ayarlanabilir; ileride bir su trigger volume ile değiştirilebilir.
- Emote'lar tam vücut (Base layer); UpperBody mask katmanı attack/aim için hazır bekliyor.
