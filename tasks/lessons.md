# Dersler (TideHunters)

Tekrarlanan hatalardan kaçınmak için oturumlardan çıkarılan kurallar.

## Unity / Serialization
- **MonoBehaviour'a yeni `[SerializeField]` alan eklerken**: prefab üzerinde zaten duran bir
  bileşene yeni alan eklediğinde, C# field initializer değeri (ör. `= MotorConfig.Default`) her
  zaman deserialize sırasında çalışmayabilir; alan `default(T)` (sıfır) gelebilir. Kritik
  varsayılanları bir editor script'iyle (reflection/SerializedObject) prefab'a **açıkça yaz**.
  (2026-07-05: PlayerController.motor bu yüzden PrefabSetup'ta açıkça set edildi.)

## Play-mode otomatik test
- Manuel `InputSystem.QueueStateEvent + InputSystem.Update()`, **sürekli** okunan action'larda
  (Move, `ReadValue`) çalışır; ama **edge** action'larda (`WasPressedThisFrame` — Jump/Emote)
  güncelleme zamanlaması uyuşmadığı için güvenilmez tetiklenir. Edge mantığını doğrudan
  state/field kontrolüyle veya saf fonksiyon (PlayerMotor.Simulate) birim testiyle doğrula.
- Geçici animasyon state'lerini (Airborne gibi) MCP round-trip'leri arası (~1-14s) yakalamak
  güvenilmez; zamanlamadan bağımsız mantık testi tercih et.

## Animator inşası
- Karmaşık AnimatorController (nested 2D blend tree, maskeli katman, AnyState geçişleri) için
  granular `animator-modify` MCP çağrıları yerine, `script-execute` ile
  `UnityEditor.Animations.AnimatorController` + `BlendTree` kullanan tek bir editor script'i
  çok daha güvenilir ve tam kontrol sağlar.

## Bu projeye özel (TideHunters)
- **Su yüzeyi Y ≈ -1.6** (`SM_Env_Ocean_Tile_*`, shader `URPWater/Standard`, bounds.max.y≈-1.58).
  Swim mantığı buna göre kalibre: `MotorConfig.waterLevelY=-1.6`. Yanlış tahmin (-0.5) karakteri
  "havada" yüzdürüp kumsalda erken swim tetikliyordu.
- Yüzme derinliği `swimFloatDepth` (1.4) = ayakların yüzeyin ne kadar altında yüzdüğü → su çizgisi
  göğüste, kollar treading'de yüzeyde. `swimEnterDepth` (0.5) < floatDepth olmalı (osilasyon önlenir).
- **Hareket titremesi — KESİN ÇÖZÜM = per-frame owner sim**: Tick tabanlı sim + render
  interpolasyonu (snapshot buffer, dejitter saat, time-warp — hepsini denedim) owner'da ısrarla
  titredi (editörün değişken FPS'i + tick/frame uyumsuzluğu). **Doğru çözüm: owner'ın (host+client)
  simülasyonunu `Update()`'te her frame `Time.deltaTime` ile çalıştırmak** — karakter frame'le
  birebir hizalı hareket eder, interpolasyon/saat/buffer YOK → yapısı gereği pürüzsüz (cv 0.7 → 0.18).
  Host owner otoriter; client owner tahmin + reconcile (tick'te predHistory'ye kaydet, server
  broadcast'inde error kadar kaydır). Başka makineden görülen oyuncular snapshot interpolasyonu.
- **AnimatorController GUID tuzağı**: controller'ı delete+create ile yeniden inşa edince GUID değişir
  → prefab'ın Animator referansı KOPAR (karakter T-pose = bind pose, animasyon yok). Yeniden inşadan
  sonra controller'ı prefab'a MUTLAKA yeniden ata. `PrefabUtility.LoadPrefabContents +
  SaveAsPrefabAsset` freshly-created controller ref'iyle persist ETMEDİ; **doğrudan prefab asset'ine
  `new SerializedObject(anim).FindProperty("m_Controller").objectReferenceValue = ctrl`** çalıştı.
  (Play mode'da da prefab asset edit'i persist etmez — edit mode'da yap.)
- **Hareket modeli = rotate-to-move (adventure), strafe DEĞİL**: Kullanıcı sonunda karakterin
  gittiği yöne dönüp hep ileri koşmasını istedi (sudaki gibi kara için de). `PlayerMotor` artık
  kamera-göreli input yönüne doğru `MoveTowardsAngle` ile döner ve ileri hareket eder
  (`turnSpeed` kara 500, `swimTurnSpeed` su 160). Bu strafe/çapraz blend ihtiyacını komple kaldırdı
  → animator **1D ileri locomotion** (Idle/Walk/Run), strafe klipleri kullanılmıyor. Çapraz-geri
  "ters görünme" sorunu böylece kökten gitti (2D strafe blend'in artefaktıydı; in-place kliplerle
  çözülemiyordu).
- **Dönüş nüansı (rotate-to-move + strafe lean)**: Karakter yönüne dönerken strafe/geri animasyonunu
  göstermek için animasyon MoveX/MoveY'i ham input değil, **hareket yönünün karakterin (dönmekte olan)
  facing'ine göre yerel bileşeni** olarak ver: `localDir = Euler(0,-yaw,0)*moveDir`. Sağa dönüşte
  MoveX>0 (sağ strafe lean), hizalanınca MoveY=1'e döner. Dönüş yavaş olsun ki okunabilsin (`turnSpeed`
  220). 2D FreeformDirectional2D blend + Idle 1D Speed. Düşme: `Falling = !Grounded && !IsSwimming &&
  vertVel<-0.5` bool paramı → Fall state (Falling Idle).
- **Ayak kayması (foot slide) — animasyon hızını yer hızına eşitle**: in-place locomotion kliplerinde
  animasyon adım hızı ≠ karakter hızı ise ayaklar kayar. Klibin efektif adım hızını ölç (planted
  ayağın dünya hızı ~0 olana dek animator.speed ayarla; ayak kemiği: `Animator.GetBoneTransform(
  HumanBodyBones.LeftFoot)`). Ölçüm: Walking ~1.4, Running ~3.6 m/s efektif. Karakter hızını klibe
  yaklaştır (walk 1.9/run 4.6) + `animator.speed = groundSpeed/clipStride` (~1.35x) → kayma 0.47→0.26.
  Not: min(iki ayak) metriği swing/çift-destek fazında gürültülü, tam 0 ölçülmez.
- **Dönüş smoothness vs lean görünürlüğü — temel gerilim**: eased turn (LerpAngle exp, `turnSharpness`)
  pürüzsüz+responsive ama hızlı olduğu için strafe/backpedal lean'i damping yakalayamadan yok eder
  (180° reverse'te backpedal MoveY negatif olmuyor). Sabit-hız dönüş lean'i gösterir ama daha mekanik.
  Lean = hareket yönünün facing'e göre yerel bileşeni (`Euler(0,-yaw,0)*moveDir`), deadzone+gain ile
  gate'lenir (sadece gerçek dönüşte lean). Backpedal göstermek yavaş (laggy) dönüş ister → responsive
  ile çelişir; AAA'da 180° için genelde ayrı pivot klibi kullanılır.
- **Cinemachine ThirdPersonFollow AvoidObstacles player'a yapışıyor**: Kamera obstacle spherecast'i
  player'ın KENDİ collider'larına takılırsa mesafe ~0'a çöker (TPS bozulur). IgnoreTag TEK tag kontrol
  eder; player kökü "Player" olsa bile **child collider'lar (ör. PlayerVisual CapsuleCollider) Untagged
  kalırsa** yakalanır. Çözüm: **tüm hiyerarşiyi recursive "Player" tagle** (`GetComponentsInChildren<
  Transform>(true)`). Sonra açık alanda mesafe tam (~4), sadece gerçek objelerde kayar. Su collider'sız
  olduğundan kamera-su için ayrı `CameraWaterClamp` (Main Camera'da, exec order 10000, Y'yi yüzey+margin
  üstünde tutar).
- **UnityTransport port takılması**: hızlı play→stop→StartHost döngüsü UDP portunu (7777) OS'de
  takar → "network transport start failure". Portu değiştir (`SetConnectionData`) veya editörü
  yeniden başlat. Sahne portu 7778'e alındı.
- **Deniz dalgası bobbing (senkron)**: URPWater shader'ı `URPWaterGerstnerSimple.hlsl` Gerstner
  kullanır (`_DISPLACEMENTMODE_GERSTNER`; params `_WaveCount=4, _WaveLength=4.22,
  _WaveSteepness=0.105, _WaveSpeed=20, _WaveAmplitude=0.431`; zaman `_Time.x = saniye/20`). Bu
  matematik **kendi** `OceanWaves.cs`'imizde yeniden yazıldı (üçüncü taraf script KULLANILMADAN).
  Karakter ve su AYNI Gerstner'ı aynı XZ+zamanda hesaplayınca göreli batma sabit kalır → karakter
  her dalgada yüzeyde, senkron, ne batar ne havada kalır. Bob görsel-only (iskelet "Root"
  child'ına uygulanır; kamera sallanmaz), ağa gitmez.

## Netcode for GameObjects 2.x
- `NetworkBehaviour`'da `OnNetworkTick` YOK → `NetworkManager.NetworkTickSystem.Tick` event'ine
  abone ol (OnNetworkSpawn'da +=, OnNetworkDespawn'da -=).
- RPC: `[Rpc(SendTo.Server)]` / `[Rpc(SendTo.Owner)]` (klasikler `[ServerRpc]` obsolete).
- Client-side prediction için `NetworkTransform`'u kaldırıp özel replikasyon (NetworkVariable +
  reconciliation) kur; NetworkTransform prediction ile çakışır.

## ScriptableObject asset kırılganlığı (WeaponDatabase tekrar bozulması)
- **Semptom**: Alınan itemlar hotbar slotlarında görünmez (ikon yok) + gun alsan bile sol tık sword
  slash oynatır. Kök neden: `WeaponDatabase.Instance` (Resources.Load) **null** →
  `FindAssets("t:WeaponDatabase")=0`, asset diskte olsa da tip çözülmüyor (m_Script referansı bozuk).
  Null olunca `IconOf`→null (ikon yok) ve eski `SelectedCategory` fallback'i `Sword`'du (gun→slash).
- **Kök neden**: `WeaponDatabase` (ScriptableObject) `WeaponTypes.cs` içindeydi. **Unity bir
  ScriptableObject/MonoBehaviour'ın script referansını DOSYA ADINA bağlar** — sınıf-adı≠dosya-adı olunca
  m_Script linki kırılgan olur ve reimport/domain-reload'da tipini kaybeder (bu yüzden tekrar tekrar
  bozuluyordu). **Kalıcı çözüm**: `WeaponDatabase`'i kendi `WeaponDatabase.cs` dosyasına taşı (enum'lar +
  `WeaponDef` `WeaponTypes.cs`'te kalabilir — onlar SO/MB değil). Doğrulama: ForceUpdate reimport sonrası
  `LoadAssetAtPath=True, FindAssets=1` (eskiden bozuluyordu). Geri birleştirme.
- **Savunma**: kategori gibi kritik davranışı DB'den değil id enum'undan türet
  (`WeaponDatabase.CategoryFor(id)`: WoodenSword/GoldenSword=Sword, yoksa Gun) → DB bozulsa bile
  gun asla sword gibi davranmaz. İkon DB gerektirir, o yüzden asset yine de sağlam olmalı.
- **Genel ders**: bir kullanıcı "genel bozulma, geri al" derse körü körüne revert etme — semptomlar
  (ikon yok + yanlış kategori) tek bir ortak nedene (DB null) işaret ediyordu; asıl refactor (gun aim)
  suçsuzdu. Önce kök nedeni bul.

## Taşınan/tutulan prop (sandık taşıma sistemi)
- **Tutulan obje karakter KÖKÜNE bağlanırsa sway yapmaz** (havada/sabit görünür). Kullanıcı "sandık
  benimle sallanmıyor, sabit duruyor" dedi. **Çözüm: her `LateUpdate`'te (animator iskeleti pozladıktan
  SONRA) objeyi iki el kemiğinin ortasına yerleştir** (`(GetBoneTransform(LeftHand).position +
  RightHand.position)*0.5`). Kollar/gövde animasyonla sallanınca obje de birebir takip eder → gerçekten
  taşınıyor gibi. Root-child + sabit local offset YANLIŞ (idle sway'i kaçırır).
- **Runtime inspector-tunable offset**: per-item taşıma offset'ini (position/euler/scale) Resources'tan
  yüklenen ScriptableObject'te (ChestDatabase) tut ve `LateUpdate`'te **her frame CANLI oku** (build
  anında bir kez değil). Böylece kullanıcı oyun çalışırken asset'in inspector'ından offset'i değiştirip
  anında sonucu görür; SO asset edit'leri play mode'da da PERSIST eder. (Component'e gömmek yerine
  per-type olduğu için DB doğru yer.)
- **Fiziksiz drop'u zemine oturt (pivot-bağımsız)**: obje pivot'u tabanda değilse, raycast hit noktasına
  pivot koymak objeyi havada/gömük bırakır. **Doğru: raycast ile zemin Y'sini bul, objeyi instantiate et,
  `Physics.SyncTransforms()`, sonra `pos += up * (groundY - collider.bounds.min.y)`** → collider TABANI
  tam zemine oturur (pivot nerede olursa olsun). Ağırlıklı/rigidbody'siz sandıkta gerekli.
  - **Diagnostic tuzağı**: bir objeyi yerleştirdikten SONRA onun üstünden aşağı raycast atıp "zemin"i
    ölçme — ışın objenin KENDİ collider'ına çarpar, sahte groundY verir. Zemini obje spawn olmadan ÖNCE
    (drop noktasında) ölç.
- **Taşınan obje boyutu**: kullanıcı taşınırken de dünyadakiyle AYNI boyut istedi → `carryScale=1`
  (küçültme yok). Elde küçülüp yerde büyümesi kötü hissettiriyordu.
- **Local cosmetic klon (ağ objesi prefab'ından)**: taşınan görsel, networked chest prefab'ının yerel
  kopyası. `NetworkObject`/`WorldChest`/`Rigidbody`'yi **`DestroyImmediate` ile parent'lanmadan ÖNCE**
  kaldır (spawned player altında nested NetworkObject uyarısı olmasın). `WorldChest`'i NetworkObject'ten
  ÖNCE sil (RequireComponent ile tutuyor). Collider'ları disable et. Bu klon asla Spawn edilmez.
- **İzole ikon render (URP)**: prefab'ı hotbar ikonuna çekerken sahne geometrisini (yeşil zemin)
  yakalamamak için: instance'ı uzak bir konuma (9000,9000,9000) taşı + tüm hiyerarşiyi boş bir layer'a
  (31) al + kamera & ışık `cullingMask = 1<<31`. Render: `RenderPipeline.SubmitRenderRequest(cam,
  new RenderPipeline.StandardRequest{destination=rt})` (URP'de `Camera.Render()` çalışmaz). Şeffaf zemin:
  `clearFlags=SolidColor, bg=(0,0,0,0)`, `RenderSettings.fog=false`. ARGB32 RT → ReadPixels → EncodeToPNG
  → TextureImporter type=Sprite, alphaIsTransparency=true.
- **In-scene NetworkObject spawn**: sahneye elle konmuş prefab instance'ına NetworkObject eklenince
  (EnableSceneManagement=true iken) host StartHost'ta otomatik spawn eder (WorldChest.Active 3 oldu).
  Runtime drop-spawn için prefab AKTİF `NetworkPrefabsList`'e kayıtlı olmalı (bu projede iki kopya var;
  NetworkManager'ın `NetworkConfig.Prefabs.NetworkPrefabsLists[0]` referansı esas =
  `Assets/Game/ScriptableObjects/NetworkSO/DefaultNetworkPrefabs.asset`).
