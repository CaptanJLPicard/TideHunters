# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Proje

**TideHunters** — erken aşamada bir Unity çok oyunculu (multiplayer) oyunu. Unity **6000.3.15f1** (Unity 6.3), Universal Render Pipeline, Netcode for GameObjects. Kod tabanının büyük çoğunluğu üçüncü taraf eklentilerinden oluşur; asıl oyun kodu küçüktür ve tamamen `Assets/Game/` altında yer alır.

## Unity Editor ile Çalışma (Unity MCP)

Bu proje bir **Unity MCP köprüsüne** (`com.ivanmurzak.unity.mcp`) bağlıdır; köprü HTTP üzerinden `http://localhost:28900` adresinde açıktır (bkz. `.mcp.json`). `mcp__ai-game-developer__*` / `mcp__unity-mcp__*` araçları ve karşılık gelen skill'ler (`gameobject-*`, `scene-*`, `assets-*`, `script-*`, `editor-application-*`, `tests-run` vb.) **canlı Editor'ü** yönetir — GameObject, sahne, prefab ve asset oluşturup inceleme, play mode çalıştırma ve C# yürütme.

- Bu araçlar **Unity Editor'ün açık ve bu projenin yüklü olmasını** gerektirir. Bir araç "bağlantı yok" hatası verirse Editor kapalıdır ya da köprü çalışmıyordur.
- `.asset`/`.unity`/`.prefab` YAML dosyalarını elle düzenlemek yerine bu MCP araçlarını tercih et — bunlar serialize edilmiş Unity nesneleridir ve elle düzenlerken kolayca bozulur.
- Diskte `.cs` dosyalarını değiştirdikten sonra Unity'nin yeniden derlemesini zorlamak için `assets-refresh` kullan, ardından derleme hataları için `console-get-logs`'a bak.
- Bir değişikliği Editor içinde doğrulamak için onu çalıştır: `editor-application-set-state` (play mode'a gir/çık) ve `screenshot-game-view`.

## Derleme / Test

npm/make gibi bir sarmalayıcı yoktur — bu bir Unity projesidir. Derleme ve test Editor üzerinden veya Unity CLI batch mode ile yapılır.

- **Testler**: Unity Test Framework (`com.unity.test-framework`). `tests-run` MCP aracı ya da Editor Test Runner penceresi üzerinden çalıştırılır. Şu anda **oyuna ait test yoktur**.
- **Çok oyunculu test**: Editor içinde birden fazla sanal oyuncu ayağa kaldırmak için `com.unity.multiplayer.playmode` (Multiplayer Play Mode) ve `com.unity.multiplayer.tools` kuruludur.
- CLI batch build örneği: `Unity.exe -quit -batchmode -projectPath . -executeMethod <BuildClass.Method>` (henüz bir build script'i yoktur).

## Mimari

### Ağ (Netcode for GameObjects)

- Host/client topolojisi. `TestingNetcodeUI` (`Assets/Game/Scripts/`) iki UI düğmesini `NetworkManager.Singleton.StartHost()` / `StartClient()` çağrılarına bağlar — bir oturuma girişin şu anki noktası budur.
- `PlayerController : NetworkBehaviour`, mantığı `IsOwner` üzerinden koşullar ve doğrudan `transform.position` yazar. Hareket **client-authoritative**'tir (sahip olan istemci kendini hareket ettirir; sunucu tarafında hiçbir doğrulama yapılmaz). Ağ üzerinden senkronize durum eklemeden önce bu deseni aklında tut — yeni senkron durum için ham transform yazımı yerine `NetworkVariable`/RPC kullanılmalıdır.
- Ağ prefab kaydı `DefaultNetworkPrefabs.asset` içinde tutulur. **İki kopya vardır** — biri `Assets/DefaultNetworkPrefabs.asset`, diğeri `Assets/Game/ScriptableObjects/NetworkSO/DefaultNetworkPrefabs.asset`. Yeni bir prefab eklemeden önce aktif sahnedeki `NetworkManager`'ın hangisine referans verdiğini doğrula.
- Aktif/build sahnesi `Assets/Game/Scenes/GameScene.unity`'dir (`SampleScene` build ayarlarında devre dışıdır).

### Depo Yapısı

- `Assets/Game/` — **tüm birinci taraf içerik**: `Scripts/`, `Scenes/`, `Prefabs/` (ör. `Player.prefab`), `Materials/`, `ScriptableObjects/` (PC + Mobile için URP renderer/RP asset'leri, ağ SO'ları).
- `Assets/ArtPlugins/`, `Assets/EditorPlugins/`, `Assets/Plugins/` — üçüncü taraf asset'ler (Febucci Text Animator, AllIn1 VFX Toolkit, vFolders/vHierarchy/vInspector editor araçları, NuGet DLL'leri). Vendor edilmiş olarak kabul et; değiştirmekten kaçın.
- Oyun script'lerinin **assembly definition'ı yoktur**, bu yüzden varsayılan `Assembly-CSharp` içinde derlenirler. Her eklentinin kendi `.asmdef`'i vardır (izole assembly). `Assets/Game/` altına bir `.asmdef` eklemek, Netcode/diğer assembly'lere açıkça referans vermeyi gerektirir.

### Girdi (Input)

Yeni Input System (`com.unity.inputsystem`) kuruludur ve `Assets/InputSystem_Actions.inputactions` mevcuttur, **ancak `PlayerController` hâlâ eski (legacy) `Input.GetKey(...)` okur**. Girdi eklerken projenin aktif input handling backend'ini kontrol et ve tutarlılık için legacy yolu genişletmek yerine Input System action asset'ini tercih et.

## Konvansiyonlar

- Birinci taraf runtime script'leri şu anda namespace kullanmaz ve varsayılan `Assembly-CSharp`'tadır. Çevredeki dosyanın stiline uy.
- `.cs` dosyaları diskte (bu depoda) düzenlenir, ancak sahneler, prefab'lar ve `.asset` dosyaları metin olarak düzenlenmek yerine Unity MCP araçları üzerinden değiştirilmelidir.

### Kurallar
- **Benimle konuşurken**: benimle her zaman türkçe konuşacaksın, başka bir dilde konuşmak yasak!
- **Senin Hakkında**: sen bu projede geliştirme yaparken sektörün en önde gelen Senior Unity Game Developerı olarak geliştirme yapıcaksın
- **fotoğraflara bak** denilirse: "C:\Users\hakan\OneDrive\Masaüstü\TideHunter_IMAGE" bu yolda bulunan dosyanın içindeki fotoğrafları incelemeni söylüyorum, bu dosyaya gir ve içindeki fotoğrafları incele.
- **Bana soru sorarsan**: eğer bana soru sorarsan plan modda vb sorduğun soru düz text olmasın panelli bir şekilde soru sor yani şıklar arasında ok yön tuşları ile gezip enter ile şıkları seçebileyim.
- **Git üzerinden commit atarken**: Bana sormadan asla git/github üzerinde herhangi bir değişiklik yapmayacaksın, git/github üzerinde en ufak bir dğeişiklik yapmak istersen bile benden izin almak zorundasın.
- **Github Üzerinden Commit Atmak Yasak**: Github üzerinden herhangi bir şekilde değişiklik yapmak, commit atmak yasak bunu yapmanı direk olarak istemiyorum, bunu hiçbir zaman asla yapma!

### 1. Varsayılan Plan Modu
– Basit olmayan HER görev için plan moduna gir (3+ adım veya mimari kararlar)
– Bir şey ters giderse DUR ve yeniden planla — körü körüne devam etme
– Plan modunu sadece inşa için değil, doğrulama adımları için de kullan
– Belirsizliği azaltmak için baştan detaylı spesifikasyon yaz

### 2. Alt-Ajan Stratejisi
– Ana bağlam penceresini temiz tutmak için alt-ajanları bol bol kullan
– Araştırma, keşif ve paralel analizi alt-ajanlara yükle
– Karmaşık problemlerde alt-ajanlarla daha fazla işlem gücü harca
– Odaklı yürütme için her alt-ajana tek bir görev ver

### 3. Kendini Geliştirme Döngüsü
– Kullanıcıdan HERHANGİ bir düzeltme sonrası: `tasks/lessons.md`yi güncelle
– Aynı hatanın tekrarını önleyen kurallar yaz
– Hata oranı düşene kadar bu dersleri acımasızca geliştir
– Her oturum başında ilgili projenin derslerini gözden geçir

### 4. Tamamlanmadan Önce Doğrulama
– Çalıştığını kanıtlamadan bir görevi asla tamamlandı olarak işaretleme
– Gerektiğinde ana dal ile değişikliklerin arasındaki farkı kontrol et
– Kendine sor: "Kıdemli bir mühendis bunu onaylar mıydı?"
– Testleri çalıştır, logları kontrol et, doğruluğu kanıtla

### 5. Zarafet Talep Et (Dengeli)
– Basit olmayan değişikliklerde dur ve sor: "Daha zarif bir yol var mı?"
– Çözüm yamalı hissediyorsa: "Şu an bildiklerimle zarif çözümü uygula"
– Basit, bariz düzeltmelerde bunu atla — aşırı mühendislik yapma
– Sunmadan önce kendi işini sorgula

### 6. Otonom Hata Düzeltme
– Hata raporu verildiğinde: direkt düzelt. El tutulmasını bekleme
– Loglara, hatalara, başarısız testlere bak — sonra çöz
– Kullanıcıdan sıfır bağlam değişikliği gereksin
– CI testleri başarısız olunca nasıl yapılacağı söylenmeden git düzelt

## Görev Yönetimi

1. **Plan Önce**: `tasks/todo.md`ye işaretlenebilir maddelerle plan yaz
2. **Planı Doğrula**: Uygulamaya başlamadan önce onayla
3. **İlerlemeyi Takip Et**: İlerledikçe maddeleri tamamlandı işaretle
4. **Değişiklikleri Açıkla**: Her adımda üst düzey özet sun
5. **Sonuçları Belgele**: `tasks/todo.md`ye inceleme bölümü ekle
6. **Dersleri Kaydet**: Düzeltmelerden sonra `tasks/lessons.md`yi güncelle

## Temel İlkeler

– **Önce Sadelik**: Her değişikliği olabildiğince basit yap. Minimal kod etkisi.
– **Tembellik Yok**: Kök nedeni bul. Geçici çözüm yok. Kıdemli standartlar.
