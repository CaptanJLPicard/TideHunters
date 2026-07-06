# TODO — Düşman AI Gemi & Mürettebat (Faz 1: SmallEnemyShip + 5 NPC)

Spec: `docs/superpowers/specs/2026-07-06-enemy-ai-ship-design.md`
Kararlar: 5 NPC · sunucu-yetkili ağ-senkron · NavMesh yok · Player sistemleri yeniden kullanılır.
(Önceki sandık-taşıma görevi tamamlandı — git log.)

## Aşama A — Temel scriptler (sahneye dokunmadan, derlensin)
- [ ] `IDamageable.cs` (TakeDamage arayüzü)
- [ ] `Health.cs` (NetworkBehaviour, 100 HP, OnChanged, OnDeath, IDamageable)
- [ ] `ShipHealth.cs` (IDamageable, HealthVisuals mesh'lerini sürer)
- [ ] `IShipPilot.cs` + `IAimSource.cs` (arayüzler)
- [ ] `DeckRider.cs` (deck-carry yardımcısı — PlayerController mantığı)
- [ ] `ShipController.cs`: `IShipPilot Pilot` kancası + `AiControlled` (geriye-uyumlu)
- [ ] Derle (`assets-refresh`) + konsol temiz mi kontrol

## Aşama B — Düşman gemisi hareket + top
- [ ] `EnemyShipAI.cs` (IShipPilot: burun oyuncuya, menzil koru; komutan iskeleti)
- [ ] `ShipCannon.cs` (menzilli CannonBall ateşi; sunucu hasar, RPC kozmetik)
- [ ] Sahne: SmallEnemyShip'e NetworkObject + ShipController + EnemyShipAI + ShipHealth; dalga paramları player ile aynı; shipWheel = SM_Prop_ShipWheel_01
- [ ] EnemyShipLargeCannon'a Muzzle noktası + ShipCannon
- [ ] `PlayerInteractor.cs`: AiControlled gemide dümen atlansın
- [ ] Test: host'ta Play → gemi yüzüyor, burun oyuncuya dönüyor, top ateşliyor (screenshot)

## Aşama C — NPC iskelet (motor + anim + deck-carry + sensör)
- [ ] `EnemyNpcAnimator.controller` (PlayerAnimator kopyası + Posture katmanı)
- [ ] `EnemyNpcController.cs` (ağ StatePayload replikasyon + PlayerMotor + PlayerAnimator + DeckRider + sensörler + Health)
- [ ] `PlayerSpineAim.cs` → IAimSource refactor; PlayerController implemente eder
- [ ] Sahne: Enemy_NPC kur (CharacterController, Health, controller, HeavyGun+Muzzle sağ ele, spine-aim) → prefab yap → 5 nokta yerleştir
- [ ] Test: 5 NPC noktalarında doğru idle, gemi sallanınca güvertede stabil (screenshot)

## Aşama D — Beyin (durum makinesi) + combat
- [ ] EnemyShipAI: PEACEFUL/ALARM/REPEL/CHEST/COUNTER-BOARD/RESET + NPC rol dağıtımı
- [ ] NPC: alarm dolaşma (kenar sensörü + NPC-NPC ayrışma), HeavyGun ateş (sunucu hitscan 20)
- [ ] NPC aboard (oyuncu gemisine suya atla + DeckBoardPoint)
- [ ] `PlayerCombat.cs`: FireRpc hitscan → IDamageable 20 (oyuncu NPC'yi vurur)
- [ ] Test: aboard→REPEL, sandık önceliği, counter-board

## Aşama E — Player Sağlık UI
- [ ] `Player.prefab`: Health (100)
- [ ] `Canvas/GamePanel` altına HealthBar (envanter slot art style)
- [ ] `GameHUD.cs`: yerel oyuncu Health.OnChanged → bar
- [ ] Test: hasar alınca bar düşüyor

## Aşama F — Cila + doğrulama
- [ ] Konsol tam temiz (compile + runtime)
- [ ] Uçtan uca oyun akışı testi (screenshot serisi)
- [ ] Adversarial kod incelemesi

## Review
Faz 1 tamamlandı ve play mode'da (host) uçtan uca doğrulandı — sıfır compile/runtime hatası.

**Mimari:** NPC = "Player'ın kopyası" — `PlayerMotor.Simulate` + `PlayerAnimator` + `StatePayload` replikasyonu +
`DeckRider` (deck-carry) birebir yeniden kullanıldı; sadece beyin/sensör/combat/sağlık yazıldı. Enemy ship
`ShipController`'ı yeniden kullanır (`IShipPilot` kancasıyla AI dümen). `PlayerSpineAim` → `IAimSource` arayüzüne
refactor edildi (NPC de aynı nişan IK'sını kullanır). Hepsi sunucu-yetkili.

**Doğrulanan davranışlar:**
- Enemy ship yüzer (Gerstner buoyancy), burnunu manned oyuncu gemisine çevirir, top **parabol** çizerek namlu
  ucundan ateşler (menzilli). Gemi ~1.1m yükseltildi (player gemisi gibi düzgün su hattı).
- 5 NPC noktalarında doğru idle: Cannon/Ammo→Kneel, Idle→Warrior, Sitting→Sit, Driver→Carry.
- Alarm→devriye (kenardan düşmez; düşerse GoHome ile geri biner; takılırsa hard-unstick).
- Repel→mesafe koru + **sıralı (staggered) HeavyGun ateşi** (komutan fire-slot) → kaçılabilir; fiziksel mermi + trail iz.
- Counter-board→suya atlayıp player gemisine aboard. Player kaçarsa/gemi boşalırsa recall → noktalara + idle → gemi tekrar sailer.
- Sağlık: player+NPC 100hp, 20 hasar; player HP UI (envanter-slot stili) Canvas'ta. Ölünce YOU DIED paneli
  (host restart / client değil / main menu), ceset güvertede stabil kalır, "Die" anim kancası hazır (kullanıcı klibi verince bağlanacak).
- Territory + "manned" kontrolü: bölge dışı ya da **boş** player gemisi hedef alınmaz; mürettebatsız gemi hareket etmez.

**Kalan (kullanıcı):** Ölüm animasyon klibi verilince `Die` state'ine bağlanacak; balans tunable'ları
(crewFireSpacing, gunDamage, ranges) inspector'dan ayarlanabilir. Git commit ATILMADI (kullanıcı izni bekleniyor).
