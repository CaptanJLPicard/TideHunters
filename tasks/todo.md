# TODO — Sandık Taşıma Sistemi (Chest Carrying, online)

(Önceki spine-aim / attack / envanter / emote işleri tamamlandı — geçmiş için git log.)

Mevcut **silah sistemini birebir aynalayan**, tamamen server-authoritative + Netcode senkron sandık taşıma.

## Kararlar (kullanıcı onaylı)
- Prompt dili: **İngilizce** (mevcut HUD stiliyle; "Press E to carry ...").
- Taşırken hareket: **yavaşla + koşamaz** (walk-only + hafif çarpan).
- Slot modeli: **serbest** — sandık bir slotta durur (ikonu görünür), 1-4 ile silaha geçilebilir. Sandık slotu seçiliyken sandık elde görünür + Carrying animasyonu oynar. G ile bırakılır.
- Animasyon: tek `Carrying.anim` (humanoid, looping) → yeni **"Carry" üst-vücut animator katmanı**, ağırlık `IsCarryingChest`'e göre 0→1. Bacaklar taban locomotion'dan.

## Yeni script'ler
- [ ] `ChestTypes.cs` — `ChestId` enum + `ChestDef`.
- [ ] `ChestDatabase.cs` — Resources ScriptableObject (kendi dosyasında).
- [ ] `WorldChest.cs` — `NetworkBehaviour`, `static Active`, `Chest`, `AimPoint`.
- [ ] `PlayerCarry.cs` — sandık görseli (CarryAnchor) + "Carry" katman ağırlığı.

## Değişecek script'ler
- [ ] `PlayerInventory.cs` — `InvState` chest slotları, `AddChestServer` (+auto-select), G drop yönlendirme, `DropChestServer`, `SelectedChest`, `IsCarryingChest`, `HasEmptySlot`, `GetChestSlot`.
- [ ] `PlayerInteractor.cs` — sandık look-at prompt + `CarryChestRpc`.
- [ ] `PlayerController.cs` — taşırken sprint kapalı + slowdown, `_inv`.
- [ ] `PlayerSpineAim.cs` — taşırken gövde eğmesini atla.
- [ ] `GameHUD.cs` — sandık slot ikonu.

## Editör/asset işleri (MCP)
- [ ] `assets-refresh` + console hata kontrolü.
- [ ] Animator: "Carry" katmanı (UpperBody mask) + "Carry" state (Carrying.anim, looping), ağırlık 0.
- [ ] Sandık prefab'ları: kök'e `NetworkObject` + `WorldChest` (chestType). Collider var.
- [ ] `ChestDatabase.asset` (Resources) — 3 def + ikonlar.
- [ ] Aktif `NetworkPrefabsList` (id 82926)'e 3 sandık prefab'ı kaydet.
- [ ] Player prefab'a `PlayerCarry`.
- [ ] Sahne kaydet (in-scene NetworkObject'ler).

## Doğrulama
- [x] Play mode host: bak→animasyonlu "Press E to carry"→E→elde+Carrying anim+slot ikonu→G bırak. (screenshot doğrulandı)
- [x] Sandık ellerin ortasını takip ediyor → gövde/kollarla sway (log: visualPos = handsMid + offset).
- [x] Doluluk: 4 slot dolu → "Not enough space" (screenshot).
- [x] In-scene 3 sandık host'ta NetworkObject olarak spawn (WorldChest.Active=3), drop→yeni WorldChest.
- [x] Bırakılan sandık zemine oturuyor (collider tabanı → zemin snap, screenshot).
- [x] Boyut sabit (carryScale=1, dünyadakiyle aynı).
- [~] Online remote (MPPM 2 instance): replike InvState'ten türetildiği için silah sistemiyle aynı mekanik; kod review ile denetlendi (canlı 2. istemci testi yapılmadı).
- [x] Taşıma offset canlı (ChestDatabase'den her frame okunur) → inspector'dan runtime tuning.
- [x] Adversarial kod review workflow (netcode + correctness) çalıştırıldı.

## Review
Silah sistemi birebir aynalandı; sandık = slot item, WorldChest = DroppedWeapon muadili, PlayerCarry =
PlayerCombat'ın carry karşılığı. Yeni ağ durumu minimal (genişletilmiş InvState + intrinsic chest tipi).
Kullanıcı iterasyonları: (1) sway → el-takibi, (2) runtime offset → ChestDatabase canlı okuma,
(3) sabit boyut → carryScale=1, (4) zemine oturma → collider-taban snap. Tümü play mode'da doğrulandı.
Kalan: taşıma offset'lerinin görsel ince ayarı kullanıcıya bırakıldı (ChestDatabase.asset).
