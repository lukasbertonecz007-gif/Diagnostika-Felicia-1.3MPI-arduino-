# Arduino firmware

Verze: **0.3**

Firmware je určený pro Arduino Nano / Uno 5 V a K-line převodník z BC547 tranzistorů.

## Piny

Knihovna AltSoftSerial na ATmega328P používá pevné piny:

- `D8` = K-line RX
- `D9` = K-line TX

USB Serial Monitor / komunikace s PC aplikací běží na `115200 baud`.

## Protokol mezi Arduinem a aplikací

Firmware vypisuje běžný surový debug log a zároveň strojově čitelné řádky:

- `APP_BOOT|Diagnostika_Felicia_KLine|0.3`
- `APP_ID_FIELD|n|hodnota`
- `APP_DTC|kod|raw bajty|status`
- `APP_DTC_NONE`
- `APP_LIVE|vzorek|rpm|napeti|klapka`
- `APP_CLEAR|OK` / `APP_CLEAR|REFUSED` / `APP_CLEAR|TIMEOUT`
- `APP_HW|OK` / `APP_HW|FAIL`

## Ověřená ECU

- Škoda Felicia 1.3 MPI
- ECU `047906030N`
- `SIMOS 2P`
- KW1281, adresa motoru `0x01`, 9600 baud po 5baud initu

## Příkazy přes Serial

- `f` - číst paměť závad
- `c` - smazat paměť závad
- `l` - živá data: otáčky, napětí, škrticí klapka
- `t` - test K-line převodníku
- `?` - menu

Mazání závad je jediná zapisovací operace a má se používat až po opsání nebo uložení závad.
