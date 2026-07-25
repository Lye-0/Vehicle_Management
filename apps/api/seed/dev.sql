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

INSERT OR IGNORE INTO maintenance_documents (id, number, type, category, status, customer_id, vehicle_id, intake_date, completion_date, issued_at, due_date, tax_rate, subtotal, tax, total, note)
VALUES ('maintenance-001', 'M-2026-118', '整備見積書', '一般整備', '受付中', 'customer-sato', 'vehicle-sato-prius', '2026-07-25', '2026-07-27', '2026-07-25', '2026-08-08', 10, 23600, 2060, 22660, '左後ドア小傷を次回点検時に確認。');
INSERT OR IGNORE INTO maintenance_documents (id, number, type, category, status, customer_id, vehicle_id, intake_date, completion_date, issued_at, due_date, tax_rate, subtotal, tax, total, note)
VALUES ('maintenance-002', 'M-2026-114', '納品書', '法定点検', '作業中', 'customer-tanaka', 'vehicle-tanaka-fit', '2026-07-24', '2026-07-26', '2026-07-24', '2026-08-07', 10, 27200, 2720, 29920, '代車：軽自動車を手配。');
INSERT OR IGNORE INTO maintenance_documents (id, number, type, category, status, customer_id, vehicle_id, intake_date, completion_date, issued_at, due_date, tax_rate, subtotal, tax, total, note)
VALUES ('maintenance-003', 'M-2026-108', '整備請求書', '車検', '完了', 'customer-suzuki', 'vehicle-suzuki-note', '2026-07-20', '2026-07-22', '2026-07-20', '2026-08-03', 10, 45000, 4500, 103350, '車検整備完了。次回オイル交換は3か月後。');

INSERT OR IGNORE INTO maintenance_items (id, document_id, item_type, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('maintenance-item-001', 'maintenance-001', '作業', 'エンジンオイル交換', 1, '式', 6800, 6800, 0);
INSERT OR IGNORE INTO maintenance_items (id, document_id, item_type, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('maintenance-item-002', 'maintenance-001', '部品', 'オイルフィルター', 1, '個', 1800, 1800, 1);
INSERT OR IGNORE INTO maintenance_items (id, document_id, item_type, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('maintenance-item-003', 'maintenance-001', '作業', '12か月点検', 1, '式', 15000, 15000, 2);
INSERT OR IGNORE INTO maintenance_items (id, document_id, item_type, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('maintenance-item-004', 'maintenance-001', '調整', '調整額', 1, '式', -3000, -3000, 3);
INSERT OR IGNORE INTO maintenance_items (id, document_id, item_type, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('maintenance-item-005', 'maintenance-002', '作業', '24か月点検', 1, '式', 24000, 24000, 0);
INSERT OR IGNORE INTO maintenance_items (id, document_id, item_type, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('maintenance-item-006', 'maintenance-002', '部品', 'ブレーキフルード', 1, '個', 3200, 3200, 1);
INSERT OR IGNORE INTO maintenance_items (id, document_id, item_type, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('maintenance-item-007', 'maintenance-003', '作業', '車検基本整備', 1, '式', 42000, 42000, 0);
INSERT OR IGNORE INTO maintenance_items (id, document_id, item_type, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('maintenance-item-008', 'maintenance-003', '部品', 'ワイパーゴム', 2, '本', 1500, 3000, 1);
INSERT OR IGNORE INTO maintenance_items (id, document_id, item_type, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('maintenance-item-009', 'maintenance-003', '法定費用', '自賠責', 1, '式', 17650, 17650, 2);
INSERT OR IGNORE INTO maintenance_items (id, document_id, item_type, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('maintenance-item-010', 'maintenance-003', '法定費用', '重量税', 1, '式', 24600, 24600, 3);
INSERT OR IGNORE INTO maintenance_items (id, document_id, item_type, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('maintenance-item-011', 'maintenance-003', '法定費用', '印紙代', 1, '式', 1800, 1800, 4);
INSERT OR IGNORE INTO maintenance_items (id, document_id, item_type, description, quantity, unit, unit_price, amount, sort_order)
VALUES ('maintenance-item-012', 'maintenance-003', '法定費用', 'リサイクル料金', 1, '式', 9800, 9800, 5);

INSERT OR IGNORE INTO payment_records (id, document_type, document_id, invoice_amount, paid_amount, payment_date, method, note)
VALUES ('payment-sales-003', '販売請求書', 'sales-invoice-003', 3534080, 120000, '2026-07-20', '銀行振込', '残金は納車日に支払い予定。');
INSERT OR IGNORE INTO payment_records (id, document_type, document_id, invoice_amount, paid_amount, payment_date, method, note)
VALUES ('payment-maintenance-003', '整備請求書', 'maintenance-003', 103350, 103350, '2026-07-22', '現金', '店頭で受領。');
