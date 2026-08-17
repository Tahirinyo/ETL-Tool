# ETL Tool MVP — Kesinleşmiş Proje Planı

## 1. Proje özeti

Bu proje, yazılımcıların ve veri personelinin CSV veya Excel dosyalarındaki verileri görsel bir arayüz üzerinden temizleyip doğrulayarak MongoDB'ye aktarabilmesini sağlayan, ASP.NET Core MVC tabanlı bir ETL aracıdır.

MVP'nin temel değer önerisi:

> Kullanıcı, tekrar kod yazmadan aynı yapıya sahip farklı CSV/Excel dosyalarına kaydedilmiş veri temizleme kurallarını uygulayabilir; geçerli kayıtları MongoDB'ye güvenli biçimde aktarabilir ve hatalı kayıtları ayrıntılı rapor olarak alabilir.

Proje iki geliştirici tarafından, Codex benzeri kodlama araçlarıyla ortak geliştirme yaklaşımı kullanılarak 15 iş gününde tamamlanacaktır.

---

## 2. Hedef kullanıcı

İlk sürümün hedef kitlesi:

- Yazılımcılar
- Veri personeli
- Sistem entegrasyonu veya veri aktarımı yapan teknik çalışanlar

Teknik olmayan son kullanıcı deneyimi, çok kullanıcılı kullanım ve müşteri tarafına açılan SaaS deneyimi MVP kapsamında değildir.

---

## 3. Ana demo senaryosu

1. Kullanıcı yeni bir pipeline oluşturur.
2. Örnek bir `.csv` veya `.xlsx` dosyası yükler.
3. Sistem dosyanın kolonlarını ve temel şemasını algılar.
4. Kullanıcı kaynak kolonları hedef MongoDB alanlarıyla eşleştirir.
5. Kullanıcı dönüşüm kuralları ekler ve dönüşümleri sürükleyerek sıralar.
6. Kullanıcı doğrulama kurallarını tanımlar.
7. Kullanıcı MongoDB database ve collection hedefini seçer.
8. Kullanıcı upsert için benzersiz alanı belirler.
9. Sistem dönüştürülmüş örnek veriyi ve örnek hata özetini gösterir.
10. Kullanıcı pipeline'ı kaydeder ve çalıştırır.
11. Sistem dosyanın tamamını arka planda batch olarak işler.
12. Geçerli kayıtlar MongoDB'ye upsert edilir.
13. Hatalı kayıtlar yüklenmez ve indirilebilir CSV raporuna yazılır.
14. Kullanıcı çalışma geçmişinden süreyi ve işlem sayılarını inceler.
15. Kullanıcı aynı pipeline'ı daha sonra yeni bir dosyayla tekrar çalıştırabilir.

---

## 4. Kesinleşmiş ürün kararları

| Konu | Karar |
| --- | --- |
| Ürün hedefi | Dengeli ve portföylük MVP |
| Mimari hedef | Sonradan genişletilebilir modüler temel |
| Kaynaklar | CSV ve modern Excel (`.xlsx`) |
| Hedef | MongoDB |
| Veri kapasitesi | 100.000 satıra kadar güvenilir batch işleme |
| Pipeline akışı | Sabit `Extract → Map → Transform → Validate → Load` |
| Görsel düzenleme | Sürüklenerek sıralanabilen dönüşüm listesi |
| Mongo bağlantısı | Tek sunucu bağlantısı sistem ayarlarında |
| Hedef seçimi | Kullanıcı database ve collection seçer |
| Yükleme yöntemi | Kullanıcının seçtiği benzersiz alana göre upsert |
| Hatalı satırlar | Geçerliler yüklenir, hatalılar raporlanır |
| Önizleme | Dönüştürülmüş örnek ve örnek hata özeti |
| Tekrar kullanım | Pipeline kaydedilir, her çalışmada yeni dosya seçilir |
| Şema değişikliği | Fark gösterilir ve kullanıcıdan yeniden eşleştirme istenir |
| Geçmiş | Özet, süre, hatalar ve indirilebilir hata CSV'si |
| Kullanıcı sistemi | Yok; yalnızca yerel veya güvenilen ortam |
| Ekip ve süre | 2 geliştirici, 15 iş günü |
| AI kodlama araçları | Tasarım ve kod geliştiricilerle ortak ilerler |

---

## 5. MVP kapsamına dahil özellikler

### 5.1 Dosya alma

- `.csv` yükleme
- `.xlsx` yükleme
- CSV delimiter seçimi: virgül, noktalı virgül veya tab
- Kaynak kültür/yerel biçim seçimi: örneğin `tr-TR` veya `en-US`
- Excel çalışma sayfası seçimi
- İlk satırın kolon başlığı kabul edilmesi
- Dosya uzantısı, boyutu ve satır sayısı kontrolü
- Dosyayı tamamıyla belleğe almadan parçalı okuma

Eski `.xls` formatı MVP'ye dahil değildir.

### 5.2 Şema algılama ve alan eşleştirme

- Kolon isimlerini algılama
- Örnek değerlerden temel veri tipi önerisi
- Kolon tutma veya silme
- Kaynak alanı hedef alana yeniden adlandırma
- Kaynak alan ile MongoDB alanını eşleştirme
- Kaydedilen kaynak şemanın pipeline içinde tutulması
- Yeni dosyanın şemasını kaydedilmiş şemayla karşılaştırma
- Eksik, yeni ve eşleşmeyen alanları kullanıcıya gösterme
- Pipeline çalıştırılmadan önce eksik eşleştirmeleri düzeltme zorunluluğu

Benzer kolonları yapay zekâyla veya fuzzy matching ile otomatik eşleştirmek MVP kapsamında değildir.

### 5.3 Dönüşüm kuralları

MVP'de aşağıdaki dönüşümler bulunacaktır:

1. Trim
2. Büyük harfe dönüştürme
3. Küçük harfe dönüştürme
4. String veri tipine dönüştürme
5. Integer/decimal veri tipine dönüştürme
6. Tarih veri tipine dönüştürme
7. Boş değere varsayılan değer atama
8. Koşula göre satır filtreleme
9. Metin içinde bul ve değiştir
10. Tekrarlanan satırları seçilen alanlara göre kaldırma

Dönüşüm kuralları eklenebilir, düzenlenebilir, silinebilir ve sürüklenerek sıralanabilir. Çalıştırma sırası pipeline tanımında açık bir `order` değeriyle saklanacaktır.

Alanları birleştirip yeni alan oluşturma MVP kapsamında değildir.

### 5.4 Doğrulama kuralları

MVP'de aşağıdaki doğrulamalar bulunacaktır:

- Zorunlu alan
- E-posta formatı
- Sayısal minimum ve maksimum
- Metin minimum/maksimum uzunluğu
- Tarih minimum ve maksimum aralığı
- Upsert alanı boş olamaz

Doğrulamalar dönüşümlerden sonra çalışacaktır. Böylece örneğin önce boşluk temizleme ve sayı dönüşümü, ardından sayısal aralık kontrolü uygulanacaktır.

Regex ile kullanıcı tanımlı özel doğrulama MVP kapsamında değildir.

### 5.5 Önizleme

- İlk 100 satır üzerinde dönüşüm ve doğrulama önizlemesi
- Dönüştürülmüş alanların tablo görünümü
- Önizleme örneğindeki geçerli, hatalı ve filtrelenmiş kayıt sayıları
- Satır bazında kısa hata açıklaması
- Pipeline çalıştırılmadan önce ayar hatalarının gösterilmesi

Önizleme tam dosyada dry-run değildir. Tam dosyanın gerçek sonuçları yalnızca çalıştırma sırasında hesaplanacaktır.

### 5.6 MongoDB'ye yükleme

- Tek MongoDB sunucusuna uygulama ayarları üzerinden bağlantı
- Kullanıcının izin verilen database ve collection'ı seçmesi
- Upsert anahtarı olacak çıktı alanının seçilmesi
- Upsert alanının boş olamayacağının doğrulanması
- Kayıtların toplu `BulkWrite` işlemleriyle yazılması
- Insert edilen ve güncellenen kayıt sayılarının ayrı tutulması
- Tekrar çalıştırmada aynı benzersiz anahtarlı kayıtların çoğalmaması

Metadata için ayrılmış sistem database'i hedef olarak seçilemeyecektir. MongoDB connection string pipeline dokümanlarında saklanmayacak, ortam değişkeninden veya secret yapılandırmasından okunacaktır.

### 5.7 Arka plan çalıştırma ve ilerleme

- Web isteğinden bağımsız arka plan işi
- Durumlar: `Queued`, `Running`, `Completed`, `PartiallyCompleted`, `Failed`
- Her batch sonunda işlenen satır ve ilerleme bilgisinin güncellenmesi
- MVC ekranının belirli aralıklarla durum endpoint'ini sorgulaması
- Aynı uygulama örneği içinde çalışan basit iş kuyruğu
- Sistem yeniden başlarsa yarım kalan işin açıkça `Interrupted/Failed` olarak işaretlenmesi

Dağıtık kuyruk, RabbitMQ, Kafka ve birden fazla worker instance'ı MVP kapsamında değildir.

### 5.8 Çalışma geçmişi ve hata raporu

Her çalıştırma için şunlar saklanacaktır:

- Pipeline kimliği ve pipeline adı
- Kaynak dosyanın güvenli adı
- Başlangıç ve bitiş zamanı
- Toplam süre
- Durum
- Toplam satır
- Başarılı satır
- Hatalı satır
- Filtrelenmiş satır
- Yinelenen olduğu için kaldırılan satır
- Insert sayısı
- Update sayısı
- Sistemsel hata özeti
- İndirilebilir hata raporunun konumu

Hata CSV'sinde en az şu alanlar olacaktır:

- Kaynak satır numarası
- Upsert anahtarı varsa değeri
- Hatalı alanlar
- Hata nedenleri
- Orijinal satır verisi

Her başarılı satır için ayrıntılı log tutulmayacaktır; bu, veri hacmini gereksiz büyütür.

---

## 6. Kapsam dışı özellikler

Aşağıdakiler MVP'ye eklenmeyecektir:

- Serbest node canvas ve node bağlantıları
- Zamanlanmış veya periyodik pipeline çalıştırma
- SQL, REST API, JSON, XML veya FTP kaynağı
- MongoDB dışında hedef sistem
- Birden fazla MongoDB bağlantı profili
- Kullanıcı girişi, roller ve yetkilendirme
- Çok kiracılı SaaS yapısı
- Milyonlarca satır veya dağıtık veri işleme
- RabbitMQ, Kafka veya dağıtık worker
- Yapay zekâyla kolon eşleştirme
- Alan birleştirme ve özel kod çalıştırma
- Regex tabanlı kullanıcı doğrulaması
- Tam dosyada yüklemesiz dry-run
- Undo/redo destekli gelişmiş görsel editör
- Veri soy ağacı/data lineage
- Cloud deployment ve production SLA

Bu maddelerden biri ancak zorunlu kapsam eksiksiz tamamlanırsa sonraki sürüm adayı olarak ele alınabilir.

---

## 7. Önerilen teknik mimari

### 7.1 Mimari yaklaşım

Uygulama bir **modüler monolith** olacaktır. Tek deploy edilebilir uygulama olarak hızlı geliştirilecek; ancak ETL motoru MVC arayüzünden ayrılarak daha sonra yeni extractor, dönüşüm ve loader eklenebilmesine izin verecektir.

Önerilen solution yapısı:

```text
EtlTool.sln
├── EtlTool.Web
│   ├── Controllers
│   ├── Views
│   ├── ViewModels
│   └── wwwroot
├── EtlTool.Application
│   ├── Pipelines
│   ├── Execution
│   ├── Transformations
│   ├── Validations
│   └── Interfaces
├── EtlTool.Domain
│   ├── Entities
│   ├── Enums
│   └── ValueObjects
├── EtlTool.Infrastructure
│   ├── FileExtraction
│   ├── MongoDB
│   ├── BackgroundJobs
│   └── Reports
├── EtlTool.UnitTests
└── EtlTool.IntegrationTests
```

Bu yapı Clean Architecture'ın bütün törenlerini uygulamak yerine sorumlulukları net ayıran sade bir katmanlama kullanacaktır.

### 7.2 MVC sorumlulukları

- **Model:** Pipeline tanımları, kurallar, şema ve çalışma geçmişi
- **View:** Razor Views ile pipeline oluşturma, önizleme, durum ve geçmiş ekranları
- **Controller:** HTTP isteğini doğrulayıp Application servislerine yönlendiren ince controller'lar

ETL iş mantığı controller içinde yazılmayacaktır.

### 7.3 Arayüz teknolojisi

- ASP.NET Core MVC
- Razor Views
- Bootstrap
- Dönüşüm sıralaması için küçük bir drag-and-drop JavaScript kütüphanesi
- İlerleme ekranı için JavaScript polling

React, Vue, Angular ve serbest canvas kütüphanesi MVP kapsamında kullanılmayacaktır.

### 7.4 Temel arayüzler

```csharp
public interface IFileExtractor
{
    IAsyncEnumerable<DataRow> ReadAsync(
        Stream stream,
        SourceOptions options,
        CancellationToken cancellationToken);
}

public interface ITransformationHandler
{
    TransformationType Type { get; }
    TransformationResult Apply(DataRow row, TransformationRule rule);
}

public interface IValidationHandler
{
    ValidationType Type { get; }
    ValidationResult Validate(DataRow row, ValidationRule rule);
}

public interface IDataLoader
{
    Task<LoadResult> UpsertBatchAsync(
        IReadOnlyCollection<DataRow> rows,
        DestinationConfig destination,
        string keyField,
        CancellationToken cancellationToken);
}
```

`EtlOrchestrator`, extractor'dan gelen satırları sırayla dönüştürür, doğrular, batch'ler ve loader'a gönderir.

### 7.5 Veri işleme akışı

```text
Upload
  → File validation
  → Extract rows
  → Apply field mapping
  → Apply ordered transformations
  → Validate transformed row
  → Invalid: error report
  → Filtered: counter only
  → Valid: add to batch
  → Batch full: MongoDB bulk upsert
  → Update run progress
```

Başlangıç batch boyutu yapılandırılabilir şekilde 1.000 kayıt olabilir. Performans testine göre değiştirilebilir; sabit kodlanmamalıdır.

---

## 8. Temel veri modelleri

### PipelineDefinition

- `Id`
- `Name`
- `Description`
- `SourceType`
- `SourceOptions`
- `ExpectedSchema`
- `FieldMappings`
- `TransformationRules`
- `ValidationRules`
- `DestinationDatabase`
- `DestinationCollection`
- `UpsertKeyField`
- `CreatedAt`
- `UpdatedAt`

### TransformationRule

- `Id`
- `Type`
- `Order`
- `SourceField`
- `Configuration`

Her transform tipi için büyük bir controller koşul bloğu oluşturmak yerine `Type → Handler` eşleştirmesi kullanılacaktır.

### ValidationRule

- `Id`
- `Type`
- `Field`
- `Configuration`
- `ErrorMessage`

### EtlRun

- `Id`
- `PipelineId`
- `Status`
- `OriginalFileName`
- `StoredFilePath`
- `StartedAt`
- `CompletedAt`
- `TotalRows`
- `ProcessedRows`
- `ValidRows`
- `InvalidRows`
- `FilteredRows`
- `DeduplicatedRows`
- `InsertedRows`
- `UpdatedRows`
- `SystemError`
- `ErrorReportPath`

---

## 9. Hata ve tutarlılık politikası

### Satır seviyesindeki hatalar

Dönüşüm veya doğrulama hatası alan satır hedefe yazılmaz. İşlem diğer satırlarla devam eder. Satır, hata raporuna nedeni ile birlikte eklenir.

### Sistem seviyesindeki hatalar

Aşağıdaki durumlar run'ı başarısız yapar:

- Dosyanın okunamaması
- Geçersiz veya tamamlanmamış pipeline tanımı
- MongoDB bağlantısının kurulamaması
- Hedef database/collection'a erişilememesi
- Batch yazımının belirlenen retry sayısından sonra başarısız olması

Önceki batch'ler yazılmış, sonraki batch başarısız olmuşsa durum `PartiallyCompleted` olacaktır. Upsert kullanıldığı için aynı dosya güvenli biçimde yeniden çalıştırılabilir.

### Duplicate politikası

- Pipeline'daki deduplication kuralı seçilen alanlara göre çalışır.
- Aynı dosyada aynı upsert anahtarı birden fazla kez gelirse deterministik olarak ilk geçerli kayıt korunur; sonraki kayıtlar duplicate sayacına eklenir.
- Hedef collection'da aynı anahtar varsa kayıt güncellenir.

### Geçici dosyalar

- Dosyalar tahmin edilemeyen, run kimliğine bağlı güvenli adlarla saklanır.
- Kaynak dosya başarılı veya başarısız çalışma tamamlandıktan sonra temizlenir.
- Hata CSV'si çalışma geçmişi silinene kadar saklanır.
- Uygulama başlangıcında sahipsiz kalmış geçici dosyalar temizlenir.

---

## 10. Çözülmesi gereken önemli problemler

| Problem | Kararlaştırılan çözüm |
| --- | --- |
| 100 bin satırda RAM tüketimi | Satır bazlı okuma ve yapılandırılabilir batch işleme |
| Uzun HTTP isteği ve timeout | Arka plan kuyruğu, run kimliği ve polling |
| Kolonların sonraki dosyada değişmesi | Şema fark ekranı ve zorunlu yeniden eşleştirme |
| Dönüşüm sırasının sonucu değiştirmesi | Açık `order` alanı, drag-and-drop ve çalıştırma öncesi referans kontrolü |
| Kirli satırların bütün işi durdurması | Satır karantinası ve indirilebilir hata CSV'si |
| Aynı dosyanın tekrar yüklenmesi | Kullanıcı seçimli benzersiz alan ve idempotent upsert |
| MongoDB bağlantı bilgisinin sızması | Ortam değişkeni/secret; pipeline içinde bağlantı dizesi yok |
| Tarih ve decimal biçim farkları | Pipeline düzeyinde kültür ve tarih biçimi ayarı |
| Batch sırasında MongoDB hatası | Sınırlı retry, `PartiallyCompleted` durumu ve güvenli yeniden çalıştırma |
| Eski pipeline'ın yeni dosyada yanlış alan kullanması | Şema karşılaştırması ve çalıştırma öncesi kural referansı doğrulaması |
| Aşırı ayrıntılı logların büyümesi | Özet metrikler ve yalnızca hatalı satırlar için rapor |

---

## 11. Ekranlar

MVP'de aşağıdaki ekranlar bulunacaktır:

1. **Dashboard/Pipeline listesi**
   - Pipeline adı, kaynak türü, hedef ve son çalışma durumu
2. **Yeni pipeline / düzenleme sihirbazı**
   - Kaynak → şema ve mapping → dönüşümler → doğrulamalar → hedef
3. **Dönüşüm editörü**
   - Kural ekleme, düzenleme, silme ve sürükleyerek sıralama
4. **Önizleme**
   - Dönüştürülmüş ilk 100 satır ve örnek hata özeti
5. **Çalıştırma ekranı**
   - Durum, ilerleme ve temel sayaçlar
6. **Çalışma geçmişi**
   - Pipeline çalışmaları ve süreleri
7. **Çalışma detayı**
   - Tüm sayaçlar, sistem hatası ve hata CSV'si indirme
8. **Şema farkı ve yeniden eşleştirme**
   - Eski/yeni kolon karşılaştırması ve mapping düzeltme

Ayrı bir gösterişli dashboard ilk hedef değildir; pipeline listesi gerekli özetleri gösterecektir.

---

## 12. 15 iş günlük geliştirme planı

### Rol dağılımı

**Geliştirici A — ETL Engine ağırlıklı**

- Extractor'lar
- Transformation engine
- Validation engine
- Batch orchestrator
- MongoDB loader/upsert
- Performans ve unit testleri

**Geliştirici B — MVC ve uygulama akışı ağırlıklı**

- Pipeline CRUD ve repository
- Razor Views ve ViewModel'ler
- Upload, mapping ve drag-and-drop ekranları
- Background job yönetimi ve durum ekranı
- Run history ve CSV raporu
- Docker ve entegrasyon testleri

İki geliştirici ilk iki gün veri sözleşmelerini birlikte belirleyecek ve her gün birbirlerinin pull request'lerini inceleyecektir.

### Gün 1 — Kapsam, sözleşmeler ve iskelet

Ortak:

- Bu planın onaylanması ve backlog'a çevrilmesi
- Örnek temiz/kirli CSV ve XLSX dosyalarının hazırlanması
- 100 bin satırlık performans test dosyasının hazırlanması
- Solution ve proje katmanlarının oluşturulması
- Git düzeni, PR kontrol listesi ve CI başlangıcı
- Pipeline, rule ve run sözleşmelerinin belirlenmesi

Çıktı: Derlenen solution, MongoDB bağlantı testi ve kabul edilmiş domain modelleri.

### Gün 2 — Persistence ve extractor sözleşmesi

Geliştirici A:

- `IFileExtractor`, `DataRow` ve source options tasarımı
- CSV extractor başlangıcı

Geliştirici B:

- MongoDB metadata repository'leri
- Pipeline CRUD servisleri ve temel MVC sayfaları

Çıktı: Pipeline kaydedilip listelenebilir; basit CSV satırları okunabilir.

### Gün 3 — CSV/XLSX ve upload

Geliştirici A:

- CSV delimiter/culture desteği
- XLSX extractor ve worksheet seçimi
- 100 bin satır için bellek kontrollü okuma deneyi

Geliştirici B:

- Güvenli dosya upload servisi
- Dosya/uzantı/limit kontrolleri
- Upload ve worksheet/delimiter seçim ekranı

Çıktı: Her iki dosya türü yüklenip kolonları ve örnek satırları okunabilir.

### Gün 4 — Şema ve mapping

Geliştirici A:

- Şema algılama ve temel tip önerisi
- Mapping uygulama servisi

Geliştirici B:

- Alan seçme, silme ve yeniden adlandırma arayüzü
- Mapping'in pipeline tanımına kaydedilmesi

Çıktı: Kaynak kolonlar MongoDB çıktı alanlarına eşlenebilir.

### Gün 5 — Transformation engine temeli

Geliştirici A:

- Handler registry ve sıralı transformation engine
- Trim, upper/lower, default ve find/replace handler'ları

Geliştirici B:

- Transformation rule CRUD ViewModel/controller akışı
- Kural kartları ve drag-and-drop sıralama

Çıktı: Kaydedilen dönüşümler doğru sırayla örnek satıra uygulanabilir.

### Gün 6 — Kalan dönüşümler

Geliştirici A:

- String/integer/decimal/date conversion
- Koşullu filter
- Seçilen alanlara göre deduplication
- Culture/date parsing hata davranışı

Geliştirici B:

- Her kural tipi için form kontrolleri
- Kural referanslarının istemci ve sunucu doğrulaması
- Düzenleme ve silme deneyimi

Çıktı: Seçilen bütün MVP dönüşümleri uçtan uca yapılandırılabilir.

### Gün 7 — Validation engine

Geliştirici A:

- Required, email, numeric range, text length, date range ve upsert-key validator'ları
- Bir satırda birden fazla hatanın toplanması

Geliştirici B:

- Validation rule oluşturma ve düzenleme ekranları
- Pipeline readiness kontrolleri

Çıktı: Dönüşümden geçen satırlar kurallara göre geçerli/hatalı ayrılabilir.

### Gün 8 — Önizleme ve hata özeti

Geliştirici A:

- İlk 100 satır için preview orchestration
- Geçerli/hatalı/filtrelenmiş sayaçları

Geliştirici B:

- Dönüştürülmüş preview tablosu
- Satır/alan bazlı hata gösterimi
- Pipeline sihirbazının uçtan uca bağlanması

Çıktı: Kullanıcı gerçek yükleme öncesinde beklenen sonucu görebilir.

### Gün 9 — Background run ve progress

Geliştirici A:

- Batch orchestrator ve cancellation altyapısı
- Run progress callback'i

Geliştirici B:

- Uygulama içi job queue/background worker
- Run durum repository'si
- Polling endpoint'i ve çalışma ekranı

Çıktı: Pipeline HTTP isteğini bloklamadan arka planda çalışır ve ilerleme gösterir.

### Gün 10 — MongoDB bulk upsert

Geliştirici A:

- Database/collection güvenlik kontrolleri
- `BulkWrite` ile upsert loader
- Insert/update sayaçları ve sınırlı retry

Geliştirici B:

- Hedef database/collection ve upsert anahtarı ekranı
- Bağlantı/erişim hata mesajları

Çıktı: Geçerli kayıtlar batch halinde hedef collection'a idempotent biçimde yazılır.

### Gün 11 — Geçmiş ve hata CSV'si

Geliştirici A:

- Satır hata modelinin kesinleştirilmesi
- CSV rapor yazıcısı ve formül enjeksiyonuna karşı güvenli çıktı

Geliştirici B:

- Run history ve run detail ekranları
- Hata CSV'si indirme endpoint'i
- Geçici dosya temizliği

Çıktı: Tam çalışma sonuçları görülebilir ve hatalı satırlar indirilebilir.

### Gün 12 — Şema farkı ve yeniden eşleştirme

Geliştirici A:

- Eski/yeni şema karşılaştırma servisi
- Mapping sonrası kural referanslarını yeniden doğrulama

Geliştirici B:

- Şema fark ekranı
- Eksik/yeni alanların yeniden eşleştirilmesi
- Düzeltilmeden çalıştırmayı engelleme

Çıktı: Kaydedilmiş pipeline değişmiş şemalı yeni dosyayla kontrollü kullanılabilir.

### Gün 13 — Test, performans ve dayanıklılık

Geliştirici A:

- Bütün transformation/validation unit testleri
- 100 bin satırlık performans ve bellek testi
- Upsert idempotency ve partial failure testleri

Geliştirici B:

- MVC/API entegrasyon testleri
- Upload güvenlik testleri
- Schema remap ve hata raporu uçtan uca testleri

Ortak:

- Bulunan darboğazların ve kritik hataların düzeltilmesi

Çıktı: Kabul testlerinden geçen release candidate.

### Gün 14 — Paketleme ve dokümantasyon

Geliştirici A:

- Teknik mimari ve ETL engine dokümantasyonu
- Extension guide: yeni extractor/transform/loader nasıl eklenir

Geliştirici B:

- Dockerfile ve Docker Compose
- Kurulum README'si
- Demo ekranlarının son düzenlemesi

Ortak:

- Temiz ortamda kurulum testi
- Demo veri setleri ve demo script'i

Çıktı: Başka bir geliştiricinin kurup çalıştırabileceği paket.

### Gün 15 — Tampon, final kabul ve sunum

- Kalan kritik hataların düzeltilmesi
- Uçtan uca demo provası
- 100 bin satırlık son test
- Kapsam dışı maddelerin tekrar doğrulanması
- Bilinen sınırlamaların yazılması
- Release etiketi oluşturulması
- Teknik sunum ve kısa kullanım videosu hazırlanması

Çıktı: Çalışan, test edilmiş ve sunulabilir MVP.

---

## 13. Codex ile çalışma yöntemi

Codex tek seferde tüm uygulamayı üretmeye çalışmayacaktır. Her görev küçük, doğrulanabilir bir iş paketi olarak verilecektir.

Her iş paketinde şu bilgiler bulunmalıdır:

- Amaç
- İlgili proje/katman
- Kullanılacak interface ve domain modeli
- Kabul kriterleri
- Kapsam dışı davranışlar
- Yazılması gereken testler
- Değiştirilmemesi gereken dosyalar

Önerilen akış:

1. Geliştirici iş paketini ve sözleşmeyi tanımlar.
2. Codex mevcut kodu inceleyip küçük bir uygulama planı çıkarır.
3. Codex implementasyonu ve testleri üretir.
4. Geliştirici diff'i okuyup mimari ve davranış açısından inceler.
5. Testler ve örnek veri çalıştırılır.
6. Diğer geliştirici pull request incelemesi yapar.
7. Kabul kriterleri sağlanınca merge edilir.

Codex çıktısı incelemeden birleştirilmeyecektir. Özellikle veri tipi dönüşümü, MongoDB upsert filtresi, dosya temizliği ve hata raporu manuel olarak gözden geçirilecektir.

---

## 14. Test stratejisi

### Unit testler

- Her transformation handler
- Her validation handler
- Culture-aware sayı/tarih dönüşümü
- Mapping ve şema karşılaştırması
- Deduplication davranışı
- Pipeline readiness kontrolü

### Integration testler

- CSV/XLSX extractor
- MongoDB repository
- Bulk upsert
- Background job durumu
- Error CSV üretimi

### Uçtan uca kabul testleri

1. Temiz CSV → bütün satırlar MongoDB'ye gider.
2. Kirli CSV → geçerliler gider, hatalılar raporlanır.
3. Aynı dosya iki kere → duplicate document oluşmaz.
4. Değişmiş kolonlu dosya → schema remap ekranı açılır.
5. 100 bin satır → bellek taşması ve HTTP timeout olmadan tamamlanır.
6. MongoDB kesintisi → doğru run durumu ve anlaşılır hata oluşur.
7. Dönüşüm sırası değiştirilince preview ve gerçek run aynı sonucu verir.
8. CSV ve XLSX aynı mantıksal veride aynı hedef dokümanları üretir.

---

## 15. Definition of Done

MVP tamamlanmış sayılmak için:

- CSV ve XLSX kaynağı çalışmalıdır.
- Pipeline oluşturma, düzenleme, silme ve tekrar kullanma çalışmalıdır.
- Mapping, bütün seçilen dönüşümler ve doğrulamalar çalışmalıdır.
- Drag-and-drop sonrası dönüşüm sırası kalıcı olmalıdır.
- Preview ile gerçek run aynı kuralları kullanmalıdır.
- 100 bin satırlık kabul testi geçmelidir.
- MongoDB bulk upsert duplicate üretmemelidir.
- Hatalı satırlar hedefe yazılmamalı ve CSV olarak indirilebilmelidir.
- Şema değişikliğinde yeniden eşleştirme çalışmalıdır.
- Run history ve progress doğru sayaçları göstermelidir.
- Kritik servisler unit/integration testlere sahip olmalıdır.
- Uygulama Docker Compose ile temiz ortamda başlatılabilmelidir.
- README, mimari açıklama ve örnek demo verisi bulunmalıdır.
- Connection string repository içinde bulunmamalıdır.
- Bilinen sınırlamalar açıkça belgelenmelidir.

---

## 16. Zaman daralırsa özellik kesme sırası

Öncelikle çekirdek veri doğruluğu korunacaktır. Zaman yetmezse şu sırayla sadeleştirme yapılır:

1. Dashboard görselliği sadeleştirilir.
2. Canlı yüzde yerine yalnızca run durumu ve işlenen satır sayısı gösterilir.
3. Excel'de birden fazla worksheet seçimi kaldırılıp ilk worksheet kullanılır.
4. Bul/değiştir ve date-range validation sonraki sürüme bırakılır.
5. Drag-and-drop yerine yukarı/aşağı butonları geçici yedek olarak kullanılır.

Şunlar kesilmez:

- CSV desteği
- Mapping
- Temel dönüşüm ve validation motoru
- Batch processing
- MongoDB upsert
- Hatalı satır ayrımı
- Run sonucu
- En azından temel unit testler

---

## 17. Sonraki sürüm adayları

MVP sonrasında mantıklı geliştirme sırası:

1. REST API ve JSON kaynağı
2. SQL kaynağı
3. Zamanlanmış pipeline çalıştırma
4. Çoklu MongoDB bağlantı profili
5. Kullanıcı ve rol sistemi
6. Yeni hedefler: PostgreSQL/SQL Server
7. Dağıtık worker ve kalıcı job queue
8. Gelişmiş node tabanlı görsel editör

Node canvas ancak pipeline'ın dallanması, birden fazla kaynak/hedef veya koşullu akış gerçekten gerektiğinde eklenmelidir.

