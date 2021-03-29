//
//  AmiiboCharactersTableViewController.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 7/12/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit

class AmiiboCharactersTableViewController: UITableViewController {
    typealias AmiiboCharacter = (key: String, value: AmiiboDatabase.AmiiboJsonData)
    var amiiboCharacters: [AmiiboCharacter] = []
    var seriesFilter: String? = nil
    var pickerDelegate: LibraryPickerProtocol? = nil
    
    // MARK: UIViewController
    override func viewDidLoad() {
        super.viewDidLoad()
        
        amiiboCharacters = AmiiboDatabase.database.AmiiboData.filter({ (entry) -> Bool in
            if seriesFilter == nil {
                return true
            }
            
            return !AmiiboDatabase.fakeAmiibo.keys.contains(String(entry.key.suffix(16))) && String(entry.key.prefix(16).suffix(2)) == seriesFilter
        }).map({ (entry) -> AmiiboCharacter in
            return entry
        }).sorted(by: { (a, b) -> Bool in
            return a.value.Name < b.value.Name
        })
    }
    
    // MARK: UITableViewDataSource
    override func numberOfSections(in tableView: UITableView) -> Int {
        return 1
    }
    override func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        return amiiboCharacters.count
    }
    override func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let index = (indexPath as NSIndexPath).row
        if let tableView = self.view as? UITableView {
            let cell = tableView.dequeueReusableCell(withIdentifier: "CharacterTableCell") as! AmiiboCharacterTableViewCell
            cell.CellLabel.text = amiiboCharacters[index].value.Name
            cell.CellImage.image = TagDump.GetImage(id: amiiboCharacters[index].key)
            
            return cell
        }
        
        return UITableViewCell(frame: CGRect.zero)
    }
    override func tableView(_ tableView: UITableView, heightForRowAt indexPath: IndexPath) -> CGFloat {
        return 100.0
    }
    
    // MARK: UITableViewDelegate
    override func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        
        (view as? UITableView)?.deselectRow(at: indexPath, animated: true)
        
        if KeyFiles.hasKeys {
            let randomUidSig: (key: String, value: Data) = {
                if NTAG215Tag.uidSignatures.count > 0,
                   let element = NTAG215Tag.uidSignatures.randomElement() {
                    return element
                }
                
                // Return a fake uid/sig pair
                return (key: "0401028f0304050604", value: Data(count: 32))
            }()
            let ID = amiiboCharacters[indexPath.row].key.suffix(16)
            let dataId = Data(hex: String(ID))
            
            if let patched = try? TagDump.FromID(id: dataId).patchedDump(withUID: Data(hex: randomUidSig.key), staticKey: KeyFiles.staticKey!, dataKey: KeyFiles.dataKey!) {
                let tag = TagDump(data: Data(patched.data[0..<532] + Data(count: 8) + randomUidSig.value))!
                if pickerDelegate?.AmiiboCharacterPicked(tag: tag) ?? false == true {
                    TagInfoViewController.openTagInfo(dump: tag, controller: self)
                }
            }
        }
    }
}
