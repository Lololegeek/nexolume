# Contrat C ABI

Bibliothèque : `office_ffi.dll`, convention Cdecl, Windows x64. Aucun pointeur C# vers un document Rust n’est exposé. Le document est référencé par un identifiant opaque validé dans le registre de l’instance.

```c
typedef struct { const uint8_t* ptr; size_t len; } OfficeBuffer;
uint64_t office_create(void);
void office_destroy(uint64_t engine);
int32_t office_execute(uint64_t engine, const uint8_t* input, size_t length, OfficeBuffer* output);
void office_free(const uint8_t* buffer);
```

| Ressource | Allocation | Libération |
|---|---|---|
| Instance de moteur | Rust, office_create | SafeHandle C#, office_destroy |
| Documents | Rust, CreateDocument / OpenDocument | CloseDocument ou destruction de l’instance |
| Entrée UTF-8 | Tableau byte[] C#, valide pendant l’appel | .NET après l’appel |
| Buffer de réponse | Box<[u8]> Rust gardée dans un registre | C# appelle office_free dans un finally |

`office_create` renvoie 0 si l’instance ne peut pas être créée. `office_execute` renvoie 0 en cas de transport réussi, 1 pour arguments nuls / taille excessive, 2 pour handle inconnu, JSON invalide ou registre indisponible, 3 pour une panic interceptée. Une erreur métier valide reste une réponse JSON avec un transport 0. Le buffer de sortie est initialisé à vide avant traitement. Aucun buffer n’est à libérer lorsque le transport échoue.

L’appelant doit fournir des plages mémoire d’entrée et de sortie réellement valides. Un test de nullité ne peut pas valider un pointeur arbitraire. Les handles d’instance inconnus ne sont jamais déréférencés ; les libérations de buffers inconnus ou répétées ne déréférencent pas le pointeur fourni. Il faut néanmoins respecter le contrat de propriété et ne pas réutiliser une ancienne adresse après libération.

Les commandes utilisent des buffers UTF-8 et des longueurs explicites, sans terminateur nul requis. Les registres sont protégés par Mutex. `catch_unwind` empêche les panics récupérables de traverser office_execute et office_create. Il ne protège pas contre les accès mémoire invalides, l’épuisement mémoire ou tous les défauts natifs. Un registre empoisonné produit une erreur au lieu de poursuivre des mutations potentiellement incohérentes.

Les appels C# maintiennent le SafeHandle avec DangerousAddRef / DangerousRelease pendant l’échange. La couche OfficeBridge copie et désérialise la réponse puis libère toujours le buffer Rust. Snapshot, ouverture et sérialisation empruntent office_execute, sans export supplémentaire.
