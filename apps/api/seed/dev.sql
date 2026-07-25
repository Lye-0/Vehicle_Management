INSERT OR IGNORE INTO customers (id, customer_number, name, name_kana, postal_code, address, phone, email, memo)
VALUES ('customer-sato', 'C-DEMO001', '佐藤 太郎', 'さとう たろう', '100-0001', '東京都千代田区千代田1-1', '090-1234-5678', 'sato.taro@example.com', '土曜午前の来店が多い。メールより電話を希望。');
INSERT OR IGNORE INTO customers (id, customer_number, name, name_kana, postal_code, address, phone, email, memo)
VALUES ('customer-tanaka', 'C-DEMO002', '田中 花子', 'たなか はなこ', '231-0001', '神奈川県横浜市中区', '080-2345-6789', 'tanaka.hanako@example.com', NULL);
INSERT OR IGNORE INTO customers (id, customer_number, name, name_kana, postal_code, address, phone, email, memo)
VALUES ('customer-suzuki', 'C-DEMO003', '鈴木 一郎', 'すずき いちろう', '330-0001', '埼玉県さいたま市大宮区', '070-3456-7890', 'suzuki.ichiro@example.com', NULL);
INSERT OR IGNORE INTO customers (id, customer_number, name, name_kana, postal_code, address, phone, email, memo)
VALUES ('customer-yamada', 'C-DEMO004', '山田 恵子', 'やまだ けいこ', '210-0001', '神奈川県川崎市川崎区', '090-4567-8901', 'yamada.keiko@example.com', NULL);

INSERT OR IGNORE INTO vehicles (id, customer_id, maker, name, registration_number, chassis_number, model_year, inspection_date, mileage, body_color, displacement, transmission, memo)
VALUES ('vehicle-sato-prius', 'customer-sato', 'トヨタ', 'プリウス', '品川 500 あ 1234', 'ZVW5000001', 2020, '2026/10/15', 68420, 'パールホワイト', 1800, 'CVT', '左後ドア小傷あり。次回点検時に要確認。');
INSERT OR IGNORE INTO vehicles (id, customer_id, maker, name, registration_number, chassis_number, model_year, inspection_date, mileage, body_color, displacement, transmission, memo)
VALUES ('vehicle-sato-hilux', 'customer-sato', 'トヨタ', 'ハイラックス', '品川 300 か 5678', 'GUN1250002', 2022, '2027/04/08', 31280, 'アティチュードブラック', 2400, '6AT', '休日利用。');
INSERT OR IGNORE INTO vehicles (id, customer_id, maker, name, registration_number, chassis_number, model_year, inspection_date, mileage, body_color, displacement, transmission, memo)
VALUES ('vehicle-tanaka-fit', 'customer-tanaka', 'ホンダ', 'フィット', '横浜 300 い 5678', 'GK3000003', 2019, '2026/08/20', 42100, 'ミッドナイトブルー', 1300, 'CVT', NULL);
INSERT OR IGNORE INTO vehicles (id, customer_id, maker, name, registration_number, chassis_number, model_year, inspection_date, mileage, body_color, displacement, transmission, memo)
VALUES ('vehicle-suzuki-note', 'customer-suzuki', 'ニッサン', 'ノート', '大宮 400 う 9012', 'E1200004', 2018, '2025/12/01', 93750, 'ブリリアントシルバー', 1200, 'CVT', '車検期限を超過。早急に案内。');
INSERT OR IGNORE INTO vehicles (id, customer_id, maker, name, registration_number, chassis_number, model_year, inspection_date, mileage, body_color, displacement, transmission, memo)
VALUES ('vehicle-yamada-cx5', 'customer-yamada', 'マツダ', 'CX-5', '川崎 501 お 7890', 'KF2000005', 2021, '2027/03/31', 31200, 'ソウルレッド', 2000, '6AT', NULL);

INSERT OR IGNORE INTO sales_documents (id, number, type, status, customer_id, vehicle_id, issued_at, due_date, tax_rate, subtotal, tax, total, note)
VALUES ('sales-quote-001', 'S-2026-041', '見積書', '下書き', 'customer-sato', 'vehicle-sato-prius', '2026-07-25', '2026-08-08', 10, 2838000, 283800, 3121800, '納車時に車両取扱説明を実施。');
INSERT OR IGNORE INTO sales_documents (id, number, type, status, customer_id, vehicle_id, issued_at, due_date, tax_rate, subtotal, tax, total, note)
VALUES ('sales-order-002', 'S-2026-038', '注文書', '発行済み', 'customer-tanaka', 'vehicle-tanaka-fit', '2026-07-22', '2026-08-05', 10, 1735000, 173500, 1908500, '納車予定日は別途連絡。');
INSERT OR IGNORE INTO sales_documents (id, number, type, status, customer_id, vehicle_id, issued_at, due_date, tax_rate, subtotal, tax, total, note)
VALUES ('sales-invoice-003', 'S-2026-035', '請求書', '入金待ち', 'customer-yamada', 'vehicle-yamada-cx5', '2026-07-18', '2026-08-01', 10, 3212800, 321280, 3534080, '銀行振込での支払い。');
INSERT OR IGNORE INTO sales_documents (id, number, type, status, customer_id, vehicle_id, issued_at, due_date, tax_rate, subtotal, tax, total, note)
VALUES ('sales-quote-004', 'S-2026-029', '見積書', '発行済み', 'customer-suzuki', 'vehicle-suzuki-note', '2026-07-10', '2026-07-24', 10, 2218000, 221800, 2439800, NULL);

INSERT OR IGNORE INTO sales_document_items (id, document_id, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('sales-item-001', 'sales-quote-001', '車両本体価格', 1, '式', 2680000, 2680000, 0);
INSERT OR IGNORE INTO sales_document_items (id, document_id, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('sales-item-002', 'sales-quote-001', '付属品・特別仕様', 1, '式', 120000, 120000, 1);
INSERT OR IGNORE INTO sales_document_items (id, document_id, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('sales-item-003', 'sales-quote-001', '登録代行費用', 1, '式', 88000, 88000, 2);
INSERT OR IGNORE INTO sales_document_items (id, document_id, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('sales-item-004', 'sales-quote-001', '値引き', 1, '式', -50000, -50000, 3);
INSERT OR IGNORE INTO sales_document_items (id, document_id, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('sales-item-005', 'sales-order-002', '車両本体価格', 1, '式', 1680000, 1680000, 0);
INSERT OR IGNORE INTO sales_document_items (id, document_id, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('sales-item-006', 'sales-order-002', '納車費用', 1, '式', 55000, 55000, 1);
INSERT OR IGNORE INTO sales_document_items (id, document_id, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('sales-item-007', 'sales-invoice-003', '車両本体価格', 1, '式', 3200000, 3200000, 0);
INSERT OR IGNORE INTO sales_document_items (id, document_id, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('sales-item-008', 'sales-invoice-003', 'リサイクル料金', 1, '式', 12800, 12800, 1);
INSERT OR IGNORE INTO sales_document_items (id, document_id, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('sales-item-009', 'sales-quote-004', '車両本体価格', 1, '式', 2140000, 2140000, 0);
INSERT OR IGNORE INTO sales_document_items (id, document_id, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('sales-item-010', 'sales-quote-004', '付属品・特別仕様', 1, '式', 78000, 78000, 1);
