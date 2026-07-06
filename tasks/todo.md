# TODO — Spine Aim + Attack & Inventory System (online)

(Önceki TPS/hareket/kamera/yüzme/FOV işleri tamamlandı — geçmiş için git log.)

## Faz A — Spine aim (omurga nişan eğimi)  ✅ TAMAM
- [x] `PlayerNetTypes`: `Pitch` (InputCommand + StatePayload) — bakış açısı senkron.
- [x] `PlayerCameraRig.Pitch` property; `PlayerMotor` Pitch→state; `PlayerController.AimPitch`.
- [x] `PlayerSpineAim.cs` (exec order 200): Spine/Chest/UpperChest'i pitch'e göre eğer. Prefab'a eklendi (pitchGain 0.4, maxBend 35).
- [x] Test: lookDOWN head +0.168 (öne), lookUP -0.092 (geri) — doğru yön, doğrulandı. 0 exception.

## Faz B — Attack & Inventory (onaylı: kılıç+gun üst-beden overlay; dükkan item tükenir; boş elle saldırı yok)
### Çekirdek envanter döngüsü ✅ TAMAM (host'ta test edildi)
- [x] `WeaponTypes.cs` (WeaponId/Category/Def/Database SO) + `WeaponDatabase.asset` (Resources, 4 silah + ikonlar).
- [x] `PlayerInventory : NetworkBehaviour`: 4 slot + seçili (server-auth NetworkVariable). Kuşanılan silah Hand_R'a instantiate.
- [x] `GameHUD` hotbar (alt-orta 4 kare, 1-4 seç, vurgu, **silah adı + ikon**). Slot input (1-4).
- [x] `ShopManager` (düz sahne obj, ağ değil) + `PlayerInteractor` (E ile kuşan, server consume + HideShopItemRpc). `WorldPrompt` GameHUD'da.
- [x] Silah ikonları render edilip WeaponDatabase'e atandı (hotbar'da görünüyor).
### Kalan
- [ ] **Held weapon hold-offset tuning**: WeaponDatabase'de her silahın holdPosition/Euler/Scale'i ele oturacak şekilde ayarla.
- [ ] Drop (G): `DroppedWeapon` NetworkObject, öne/yana bırak, bobbing, E ile topla.
- [ ] **Attack + Gun aim akışı** (kullanıcı spec): sağ tık = aim → GunPlay frame ~60'a kadar oyna + DON; sol tık = 60+ oyna + ateş; sağ tık bırak = normal anim. Sword = 2 slash random, üst-beden. Attack RPC senkron. UpperBody layer.
- [ ] Emote çarkı (Z basılı → radyal + imleç → bırakınca emote); 3/4/5 emote'ları kaldır.

## Review
- [ ] Her faz play-mode host testi, 0 exception. MPPM 2-oyunculu senkron (imkan olursa).
